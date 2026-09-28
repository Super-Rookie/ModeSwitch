// Room.cs - the devices behind ModeSwitch's TV / Theatre switch: LG webOS TV, Sony network
// receiver, TP-Link Tapo smart plug, and which display Windows uses.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// ---------------------------------------------------------------- JSON helpers
static class Json
{
    static readonly JavaScriptSerializer js = new JavaScriptSerializer();
    public static string Write(object o) { return js.Serialize(o); }
    public static Dictionary<string, object> Read(string s) { return js.DeserializeObject(s) as Dictionary<string, object>; }

    // Json.Get(obj, "payload", "client-key") -> nested value, or null
    public static object Get(object o, params string[] keys)
    {
        foreach (string k in keys)
        {
            var d = o as Dictionary<string, object>;
            if (d == null || !d.TryGetValue(k, out o)) return null;
        }
        return o;
    }
}

// ---------------------------------------------------------------- LG webOS TV
// The webOS "SSAP" API: JSON over a websocket on port 3000 (3001 with TLS on newer firmware).
// The first connection shows an "allow this device?" prompt on the TV; accepting it returns a
// client key, and later connections that present the key are let in without asking.
// Powering on needs Wake-on-LAN ("Mobile TV On > Turn on via Wi-Fi" on the TV).
sealed class LgTv : IDisposable
{
    readonly string ip;
    ClientWebSocket ws;
    int nextId;

    static readonly string[] Permissions =
    {
        "APP_TO_APP", "CLOSE", "CONTROL_AUDIO", "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK",
        "CONTROL_INPUT_MEDIA_PLAYBACK", "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_TEXT", "CONTROL_INPUT_TV",
        "CONTROL_MOUSE_AND_KEYBOARD", "CONTROL_POWER", "CONTROL_TV_SCREEN", "LAUNCH", "LAUNCH_WEBAPP",
        "READ_APP_STATUS", "READ_COUNTRY_INFO", "READ_CURRENT_CHANNEL", "READ_INPUT_DEVICE_LIST", "READ_INSTALLED_APPS",
        "READ_LGE_SDX", "READ_LGE_TV_INPUT_EVENTS", "READ_NETWORK_STATE", "READ_NOTIFICATIONS", "READ_POWER_STATE",
        "READ_RUNNING_APPS", "READ_SETTINGS", "READ_TV_CHANNEL_LIST", "READ_TV_CURRENT_TIME", "READ_UPDATE_INFO",
        "SEARCH", "TEST_OPEN", "TEST_PROTECTED", "TEST_SECURE", "UPDATE_FROM_REMOTE_APP",
        "WRITE_NOTIFICATION_ALERT", "WRITE_NOTIFICATION_TOAST", "WRITE_SETTINGS"
    };

    static LgTv()
    {
        // Port 3001 uses the TV's self-signed certificate. Accept it for LG TV connections only.
        ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) =>
        {
            var req = sender as HttpWebRequest;
            return errors == System.Net.Security.SslPolicyErrors.None
                || (req != null && req.RequestUri.Port == 3001 && trustedHosts.Contains(req.RequestUri.Host));
        };
    }
    static readonly HashSet<string> trustedHosts = new HashSet<string>();

    public LgTv(string ip) { this.ip = ip; }

    // Is the TV's control service up? (It isn't while the TV is off.)
    public static bool IsUp(string ip, int timeoutMs)
    {
        using (var c = new TcpClient())
        {
            try
            {
                var ar = c.BeginConnect(ip, 3000, null, null);
                return ar.AsyncWaitHandle.WaitOne(timeoutMs) && c.Connected;
            }
            catch { return false; }
        }
    }

    // Connects and registers. With a stored key the TV lets us straight in; without one it asks on
    // screen, and we wait up to pairWaitMs for the answer. Returns null on success, else a message.
    public string Open(string clientKey, int pairWaitMs, out string key)
    {
        key = null;
        string err = ConnectWs("ws://" + ip + ":3000/");
        if (err != null)
        {
            lock (trustedHosts) trustedHosts.Add(ip);
            string err2 = ConnectWs("wss://" + ip + ":3001/");
            if (err2 != null) return "TV not reachable (" + err + ")";
        }

        var payload = new Dictionary<string, object>
        {
            { "forcePairing", false },
            { "pairingType", "PROMPT" },
            { "manifest", new Dictionary<string, object> { { "appVersion", "1.1" }, { "manifestVersion", 1 }, { "permissions", Permissions } } }
        };
        if (!string.IsNullOrEmpty(clientKey)) payload["client-key"] = clientKey;
        Send(Json.Write(new Dictionary<string, object> { { "type", "register" }, { "id", "register_0" }, { "payload", payload } }));

        var until = DateTime.UtcNow.AddMilliseconds(Math.Max(pairWaitMs, 5000));
        while (DateTime.UtcNow < until)
        {
            string text = Receive((int)Math.Max(500, (until - DateTime.UtcNow).TotalMilliseconds));
            if (text == null) break;
            var msg = Json.Read(text);
            string type = Json.Get(msg, "type") as string;
            if (type == "registered")
            {
                key = Json.Get(msg, "payload", "client-key") as string;
                return null;
            }
            if (type == "error") return "TV refused the connection: " + (Json.Get(msg, "error") ?? text);
            // "response" with pairingType PROMPT = the TV is showing its prompt; keep waiting.
        }
        return string.IsNullOrEmpty(clientKey) ? "the TV's pairing prompt wasn't accepted in time" : "the TV didn't answer";
    }

    string ConnectWs(string url)
    {
        try
        {
            ws = new ClientWebSocket();
            using (var cts = new CancellationTokenSource(4000))
                ws.ConnectAsync(new Uri(url), cts.Token).Wait();
            return null;
        }
        catch (Exception ex)
        {
            if (ws != null) { ws.Dispose(); ws = null; }
            return Inner(ex).Message;
        }
    }

    static Exception Inner(Exception ex)
    {
        while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    void Send(string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        using (var cts = new CancellationTokenSource(4000))
            ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, cts.Token).Wait();
    }

    // One whole message, or null on timeout / closed socket.
    string Receive(int timeoutMs)
    {
        var buf = new byte[16384];
        var ms = new MemoryStream();
        try
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                while (true)
                {
                    var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    t.Wait();
                    if (t.Result.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buf, 0, t.Result.Count);
                    if (t.Result.EndOfMessage) break;
                }
            }
        }
        catch { return null; }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Calls an ssap:// URI and returns its reply payload. Returns null on success, else a message.
    public string Call(string uri, object payload, out Dictionary<string, object> reply)
    {
        reply = null;
        if (ws == null) return "not connected";
        string id = "ms_" + (++nextId);
        var msg = new Dictionary<string, object> { { "type", "request" }, { "id", id }, { "uri", uri } };
        if (payload != null) msg["payload"] = payload;
        try { Send(Json.Write(msg)); }
        catch (Exception ex) { return Inner(ex).Message; }

        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            string text = Receive((int)Math.Max(500, (until - DateTime.UtcNow).TotalMilliseconds));
            if (text == null) break;
            var m = Json.Read(text);
            if (Json.Get(m, "id") as string != id) continue;
            reply = Json.Get(m, "payload") as Dictionary<string, object>;
            if (Json.Get(m, "type") as string == "error") return (Json.Get(m, "error") as string) ?? "error";
            object ok = Json.Get(reply, "returnValue");
            if (ok is bool && !(bool)ok) return (Json.Get(reply, "errorText") as string) ?? "refused";
            return null;
        }
        // turnOff often drops the connection before replying
        return uri.EndsWith("/turnOff", StringComparison.Ordinal) ? null : "no reply";
    }

    public string TurnOff() { Dictionary<string, object> r; return Call("ssap://system/turnOff", null, out r); }

    public string SwitchInput(string inputId)
    {
        Dictionary<string, object> r;
        return Call("ssap://tv/switchInput", new Dictionary<string, object> { { "inputId", inputId } }, out r);
    }

    public string Toast(string text)
    {
        Dictionary<string, object> r;
        return Call("ssap://system.notifications/createToast", new Dictionary<string, object> { { "message", text } }, out r);
    }

    // ---- sound ----
    // getVolume answers {volume, muted} on webOS 4.x and {volumeStatus: {volume, muteStatus}} on 5+.
    public string GetVolume(out int volume, out bool muted)
    {
        volume = 0; muted = false;
        Dictionary<string, object> r;
        string err = Call("ssap://audio/getVolume", null, out r);
        if (err != null) return err;
        object v = Json.Get(r, "volume") ?? Json.Get(r, "volumeStatus", "volume");
        object m = Json.Get(r, "muted") ?? Json.Get(r, "volumeStatus", "muteStatus");
        if (v == null) return "no volume in reply";
        volume = Convert.ToInt32(v);
        muted = m is bool && (bool)m;
        return null;
    }

    public string SetVolume(int volume)
    {
        Dictionary<string, object> r;
        return Call("ssap://audio/setVolume", new Dictionary<string, object> { { "volume", volume } }, out r);
    }

    public string SetMute(bool mute)
    {
        Dictionary<string, object> r;
        return Call("ssap://audio/setMute", new Dictionary<string, object> { { "mute", mute } }, out r);
    }

    // "tv_speaker", "headphone", "external_arc", "external_optical", "bt_soundbar", ...
    public string GetSoundOutput(out string output)
    {
        Dictionary<string, object> r;
        string err = Call("ssap://audio/getSoundOutput", null, out r);
        output = err == null ? Json.Get(r, "soundOutput") as string : null;
        return err;
    }

    public string SetSoundOutput(string output)
    {
        Dictionary<string, object> r;
        return Call("ssap://audio/changeSoundOutput", new Dictionary<string, object> { { "output", output } }, out r);
    }

    // appId of what's on screen, e.g. "com.webos.app.hdmi2"
    public string ForegroundApp()
    {
        Dictionary<string, object> r;
        return Call("ssap://com.webos.applicationManager/getForegroundAppInfo", null, out r) == null ? Json.Get(r, "appId") as string : null;
    }

    // "Active", "Active Standby", "Screen Off", ... (null if unknown)
    public string PowerState()
    {
        Dictionary<string, object> r;
        return Call("ssap://com.webos.service.tvpower/power/getPowerState", null, out r) == null ? Json.Get(r, "state") as string : null;
    }

    // id -> label, e.g. HDMI_2 -> "PC"
    public List<KeyValuePair<string, string>> Inputs()
    {
        var list = new List<KeyValuePair<string, string>>();
        Dictionary<string, object> r;
        if (Call("ssap://tv/getExternalInputList", null, out r) != null) return list;
        var devices = Json.Get(r, "devices") as object[];
        if (devices == null) return list;
        foreach (object d in devices)
        {
            string id = Json.Get(d, "id") as string, label = Json.Get(d, "label") as string;
            object connected = Json.Get(d, "connected");
            if (id != null) list.Add(new KeyValuePair<string, string>(id, (label ?? id) + (connected is bool && (bool)connected ? " (connected)" : "")));
        }
        return list;
    }

    // Wake-on-LAN magic packet: 6 x FF, then the MAC 16 times.
    public static string Wake(string mac, string ipHint)
    {
        byte[] m = ParseMac(mac);
        if (m == null) return "tv.mac '" + mac + "' isn't a MAC address";
        var pkt = new byte[102];
        for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Buffer.BlockCopy(m, 0, pkt, i * 6, 6);
        using (var u = new UdpClient())
        {
            u.EnableBroadcast = true;
            u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            IPAddress a;
            if (IPAddress.TryParse(ipHint, out a))
            {
                byte[] b = a.GetAddressBytes();
                b[3] = 255;                                             // x.x.x.255 (home /24 network)
                u.Send(pkt, pkt.Length, new IPEndPoint(new IPAddress(b), 9));
            }
        }
        return null;
    }

    static byte[] ParseMac(string s)
    {
        s = (s ?? "").Replace(":", "").Replace("-", "").Trim();
        if (s.Length != 12) return null;
        var b = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(s.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b[i])) return null;
        return b;
    }

    public void Dispose()
    {
        if (ws == null) return;
        try
        {
            using (var cts = new CancellationTokenSource(1000))
                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token).Wait();
        }
        catch { }
        ws.Dispose();
        ws = null;
    }
}

// ---------------------------------------------------------------- Sony receiver (Audio Control API)
// JSON-RPC over HTTP on port 10000 (STR-DN1080 and later). Powering on from standby needs the
// receiver's Network Standby setting, otherwise it drops off the network when switched off.
static class SonyAvr
{
    public static string Call(string ip, string service, string method, string version, object param, out object result)
    {
        result = null;
        try
        {
            var body = new Dictionary<string, object>
            {
                { "method", method }, { "id", 1 }, { "version", version },
                { "params", param == null ? new object[0] : new[] { param } }
            };
            var req = (HttpWebRequest)WebRequest.Create("http://" + ip + ":10000/sony/" + service);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            byte[] b = Encoding.UTF8.GetBytes(Json.Write(body));
            using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            string text;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
            var msg = Json.Read(text);
            var error = Json.Get(msg, "error") as object[];
            if (error != null) return "error " + string.Join(" ", Array.ConvertAll(error, x => Convert.ToString(x)));
            var res = Json.Get(msg, "result") as object[];
            result = res != null && res.Length > 0 ? res[0] : null;
            return null;
        }
        catch (WebException ex) { return ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.Timeout ? "not reachable (" + ip + ")" : ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }

    // on = true when the receiver is on ("active"), false in standby
    public static string GetPower(string ip, out bool on)
    {
        object r;
        on = false;
        string err = Call(ip, "system", "getPowerStatus", "1.1", null, out r);
        if (err == null) on = (Json.Get(r, "status") as string) == "active";
        return err;
    }

    public static string SetPower(string ip, bool on)
    {
        object r;
        // The STR-DN1080 only accepts "off" ("standby" is refused as Illegal Argument). Whether it
        // stays on the network afterwards (so "active" can switch it back on) depends on its Network
        // Standby and Remote Start settings - see GetPowerSettings / EnableNetworkStart.
        return Call(ip, "system", "setPowerStatus", "1.1", new Dictionary<string, object> { { "status", on ? "active" : "off" } }, out r);
    }

    // Network Standby ("quickStartMode") and Remote Start ("wolMode"). UK/EU models hide both from
    // their setup menu, but the API still reads and sets them; with both on, the receiver stays on
    // the network when switched off and can be switched on over it.
    public static string GetNetworkStart(string ip, out bool networkStandby, out bool remoteStart)
    {
        networkStandby = remoteStart = false;
        object r;
        string err = Call(ip, "system", "getPowerSettings", "1.0", new Dictionary<string, object> { { "target", "" } }, out r);
        if (err != null) return err;
        var list = r as object[];
        if (list == null) return "unexpected reply";
        foreach (object s in list)
        {
            bool on = (Json.Get(s, "currentValue") as string) == "on";
            string target = Json.Get(s, "target") as string;
            if (target == "quickStartMode") networkStandby = on;
            else if (target == "wolMode") remoteStart = on;
        }
        return null;
    }

    public static string EnableNetworkStart(string ip)
    {
        object r;
        var settings = new object[]
        {
            new Dictionary<string, object> { { "target", "quickStartMode" }, { "value", "on" } },
            new Dictionary<string, object> { { "target", "wolMode" }, { "value", "on" } }
        };
        return Call(ip, "system", "setPowerSettings", "1.0", new Dictionary<string, object> { { "settings", settings } }, out r);
    }

    // Main zone volume, its range and mute.
    public static string GetVolume(string ip, out int volume, out int min, out int max, out bool muted)
    {
        volume = min = max = 0; muted = false;
        object r;
        string err = Call(ip, "audio", "getVolumeInformation", "1.1", new Dictionary<string, object> { { "output", "" } }, out r);
        if (err != null) return err;
        var list = r as object[];
        object zone = list != null && list.Length > 0 ? list[0] : null;
        if (zone == null) return "no volume in reply";
        volume = Convert.ToInt32(Json.Get(zone, "volume"));
        min = Convert.ToInt32(Json.Get(zone, "minVolume") ?? 0);
        max = Convert.ToInt32(Json.Get(zone, "maxVolume") ?? 100);
        muted = (Json.Get(zone, "mute") as string) == "on";
        return null;
    }

    public static string SetVolume(string ip, int volume)
    {
        object r;
        return Call(ip, "audio", "setAudioVolume", "1.1",
            new Dictionary<string, object> { { "volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "output", "" } }, out r);
    }

    public static string SetMute(string ip, bool mute)
    {
        object r;
        return Call(ip, "audio", "setAudioMute", "1.1", new Dictionary<string, object> { { "mute", mute ? "on" : "off" }, { "output", "" } }, out r);
    }

    // uri e.g. "extInput:bd-dvd"
    public static string SetInput(string ip, string uri)
    {
        object r;
        return Call(ip, "avContent", "setPlayContent", "1.2", new Dictionary<string, object> { { "output", "" }, { "uri", uri } }, out r);
    }
}

// ---------------------------------------------------------------- TP-Link Tapo plug (KLAP)
// Current Tapo firmware speaks "KLAP": a two-step handshake proves both sides know the hash of the
// TP-Link account e-mail and password, then requests are AES-encrypted with keys derived from it.
// The account login is kept DPAPI-encrypted in the registry, readable only by this Windows user.
sealed class Tapo
{
    readonly string ip;
    byte[] key, iv, sig;
    int seq;
    string cookie;

    public Tapo(string ip) { this.ip = ip; }

    // ---- stored login ----
    const string RegKey = @"Software\ModeSwitch";

    public static void SaveLogin(string user, string pass)
    {
        byte[] plain = Encoding.UTF8.GetBytes(user + "\n" + pass);
        byte[] prot = ProtectedData.Protect(plain, Encoding.ASCII.GetBytes("ModeSwitch.Tapo"), DataProtectionScope.CurrentUser);
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("TapoLogin", Convert.ToBase64String(prot));
    }

    public static bool LoadLogin(out string user, out string pass)
    {
        user = pass = null;
        try
        {
            string stored;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey)) stored = k == null ? null : k.GetValue("TapoLogin") as string;
            if (stored == null) return false;
            string s = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Encoding.ASCII.GetBytes("ModeSwitch.Tapo"), DataProtectionScope.CurrentUser));
            int nl = s.IndexOf('\n');
            if (nl < 0) return false;
            user = s.Substring(0, nl);
            pass = s.Substring(nl + 1);
            return true;
        }
        catch { return false; }
    }

    // ---- protocol ----
    static byte[] Cat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }
    static byte[] Sha256(params byte[][] parts) { using (var h = SHA256.Create()) return h.ComputeHash(Cat(parts)); }
    static byte[] Sha1(byte[] b) { using (var h = SHA1.Create()) return h.ComputeHash(b); }
    static byte[] Md5(byte[] b) { using (var h = MD5.Create()) return h.ComputeHash(b); }
    static byte[] Take(byte[] b, int start, int count) { var r = new byte[count]; Buffer.BlockCopy(b, start, r, 0, count); return r; }
    static byte[] BE(int v) { return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }
    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    byte[] Post(string path, byte[] body, out int status)
    {
        var req = (HttpWebRequest)WebRequest.Create("http://" + ip + path);
        req.Method = "POST";
        req.ContentType = "application/octet-stream";
        req.Timeout = 5000;
        req.ReadWriteTimeout = 5000;
        if (cookie != null) req.Headers["Cookie"] = cookie;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException ex)
        {
            resp = ex.Response as HttpWebResponse;
            if (resp == null) throw;
        }
        using (resp)
        {
            status = (int)resp.StatusCode;
            string set = resp.Headers["Set-Cookie"];
            if (set != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(set, @"TP_SESSIONID=[^;,\s]+");
                if (m.Success) cookie = m.Value;
            }
            var ms = new MemoryStream();
            using (var s = resp.GetResponseStream()) s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    // Returns null on success, else a message.
    public string Login(string user, string pass)
    {
        try
        {
            byte[] local = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(local);
            int status;
            byte[] r1 = Post("/app/handshake1", local, out status);
            if (status != 200 || r1.Length < 48) return "handshake failed (HTTP " + status + ")";
            byte[] remote = Take(r1, 0, 16), serverHash = Take(r1, 16, 32);

            byte[] u = Encoding.UTF8.GetBytes(user), p = Encoding.UTF8.GetBytes(pass);
            byte[] authV2 = Sha256(Sha1(u), Sha1(p));                // KLAP v2 (current firmware)
            byte[] authV1 = Md5(Cat(Md5(u), Md5(p)));                // KLAP v1 (older firmware)
            byte[] auth, confirm;
            if (Same(serverHash, Sha256(local, remote, authV2))) { auth = authV2; confirm = Sha256(remote, local, authV2); }
            else if (Same(serverHash, Sha256(local, authV1))) { auth = authV1; confirm = Sha256(remote, authV1); }
            else return "the plug rejected the TP-Link login (check the e-mail and password)";

            Post("/app/handshake2", confirm, out status);
            if (status != 200) return "handshake2 failed (HTTP " + status + ")";

            key = Take(Sha256(Encoding.ASCII.GetBytes("lsk"), local, remote, auth), 0, 16);
            byte[] ivHash = Sha256(Encoding.ASCII.GetBytes("iv"), local, remote, auth);
            iv = Take(ivHash, 0, 12);
            seq = (ivHash[28] << 24) | (ivHash[29] << 16) | (ivHash[30] << 8) | ivHash[31];
            sig = Take(Sha256(Encoding.ASCII.GetBytes("ldk"), local, remote, auth), 0, 28);
            return null;
        }
        catch (Exception ex) { return "not reachable (" + ex.Message + ")"; }
    }

    byte[] Crypt(byte[] data, byte[] ivFull, bool encrypt)
    {
        using (var aes = Aes.Create())
        {
            aes.Key = key; aes.IV = ivFull; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using (var t = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                return t.TransformFinalBlock(data, 0, data.Length);
        }
    }

    public string Request(string method, object param, out object result)
    {
        result = null;
        if (key == null) return "not logged in";
        try
        {
            var body = new Dictionary<string, object> { { "method", method }, { "requestTimeMils", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds } };
            if (param != null) body["params"] = param;
            seq = unchecked(seq + 1);
            byte[] ivFull = Cat(iv, BE(seq));
            byte[] ct = Crypt(Encoding.UTF8.GetBytes(Json.Write(body)), ivFull, true);
            byte[] signature = Sha256(sig, BE(seq), ct);
            int status;
            byte[] resp = Post("/app/request?seq=" + seq, Cat(signature, ct), out status);
            if (status != 200 || resp.Length <= 32) return "request failed (HTTP " + status + ")";
            var msg = Json.Read(Encoding.UTF8.GetString(Crypt(Take(resp, 32, resp.Length - 32), ivFull, false)));
            object code = Json.Get(msg, "error_code");
            if (code != null && Convert.ToInt32(code) != 0) return "error " + code;
            result = Json.Get(msg, "result");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public string SetOn(bool on)
    {
        object r;
        return Request("set_device_info", new Dictionary<string, object> { { "device_on", on } }, out r);
    }

    // Plug name (as set in the Tapo app) and on/off state.
    public string GetInfo(out string name, out bool on)
    {
        name = null; on = false;
        object r;
        string err = Request("get_device_info", null, out r);
        if (err != null) return err;
        on = Json.Get(r, "device_on") is bool && (bool)Json.Get(r, "device_on");
        string nick = Json.Get(r, "nickname") as string;
        try { name = nick == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(nick)); } catch { name = nick; }
        return null;
    }

    // Log in with the stored login and switch. Returns null on success, else a message.
    public static string Switch(string ip, bool on)
    {
        string user, pass;
        if (!LoadLogin(out user, out pass)) return "no Tapo login saved (Room setup > Subwoofer plug login...)";
        string err = null;
        for (int attempt = 0; attempt < 3; attempt++)        // Wi-Fi plugs occasionally miss a request
        {
            var t = new Tapo(ip);
            err = t.Login(user, pass) ?? t.SetOn(on);
            if (err == null || err.StartsWith("the plug rejected", StringComparison.Ordinal)) return err;
            Thread.Sleep(1000);
        }
        return err;
    }
}

// ---------------------------------------------------------------- which display Windows uses
// SetDisplayConfig with a single path makes that display the only one (like Win+P "second screen
// only", but by name). Needs the display connected (targetAvailable).
static class DispTopo
{
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Lo; public int Hi; }
    [StructLayout(LayoutKind.Sequential)] struct SRC { public LUID a; public uint id; public uint m; public uint f; }
    [StructLayout(LayoutKind.Sequential)] struct RAT { public uint N; public uint D; }
    [StructLayout(LayoutKind.Sequential)] struct TGT { public LUID a; public uint id; public uint m; public int ot; public int rot; public int sc; public RAT rr; public int sl; public int av; public uint f; }
    [StructLayout(LayoutKind.Sequential)] struct PATH { public SRC s; public TGT t; public uint f; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct MODE
    {
        [FieldOffset(0)] public int type;            // 1 = source, 2 = target, 3 = desktop image
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID a;
        [FieldOffset(28)] public int x;              // source mode: desktop position
        [FieldOffset(32)] public int y;
    }
    [StructLayout(LayoutKind.Sequential)] struct HDR { public int type; public int size; public LUID a; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TNAME { public HDR h; public uint flags; public int ot; public ushort m; public ushort p; public uint ci; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string path; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint f, out uint p, out uint m);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint f, ref uint p, [Out] PATH[] ps, ref uint m, [Out] MODE[] ms, IntPtr t);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint p, [In] PATH[] ps, uint m, [In] MODE[] ms, uint flags);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetName(ref TNAME r);

    const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
    const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED = 0x20, SDC_SAVE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    const uint INVALID = 0xFFFFFFFF;

    static bool Query(uint flags, out PATH[] ps, out MODE[] ms)
    {
        ps = null; ms = null;
        uint pc, mc;
        if (GetDisplayConfigBufferSizes(flags, out pc, out mc) != 0) return false;
        ps = new PATH[pc]; ms = new MODE[mc];
        if (QueryDisplayConfig(flags, ref pc, ps, ref mc, ms, IntPtr.Zero) != 0) return false;
        Array.Resize(ref ps, (int)pc); Array.Resize(ref ms, (int)mc);
        return true;
    }

    static string Name(PATH p)
    {
        var n = new TNAME(); n.h.type = 2; n.h.size = Marshal.SizeOf(typeof(TNAME)); n.h.a = p.t.a; n.h.id = p.t.id;
        return GetName(ref n) == 0 ? (n.name ?? "").Trim() : "";
    }

    static bool Matches(PATH p, string nameContains)
    {
        return Name(p).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Names of the displays in use right now.
    public static List<string> Active()
    {
        var list = new List<string>();
        PATH[] ps; MODE[] ms;
        if (Query(QDC_ONLY_ACTIVE_PATHS, out ps, out ms)) foreach (var p in ps) list.Add(Name(p));
        return list;
    }

    // Names of the displays plugged in and usable, active or not (sorted, for change detection).
    public static List<string> AvailableNames()
    {
        var list = new List<string>();
        PATH[] ps; MODE[] ms;
        if (Query(QDC_ALL_PATHS, out ps, out ms))
            foreach (var p in ps)
            {
                if (p.t.av == 0) continue;
                string n = Name(p);
                if (!list.Contains(n)) list.Add(n);
            }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // Is a display with this name plugged in and powered (usable), active or not?
    public static bool Available(string nameContains)
    {
        PATH[] ps; MODE[] ms;
        if (!Query(QDC_ALL_PATHS, out ps, out ms)) return false;
        foreach (var p in ps) if (p.t.av != 0 && Matches(p, nameContains)) return true;
        return false;
    }

    // Makes the named display the only active one. Returns null on success, else a message.
    public static string ShowOnly(string nameContains, out string shownName)
    {
        shownName = null;
        PATH[] ps; MODE[] ms;

        // Already active: keep its current mode, just drop the others and move it to 0,0.
        if (Query(QDC_ONLY_ACTIVE_PATHS, out ps, out ms))
        {
            int keep = Array.FindIndex(ps, p => Matches(p, nameContains));
            if (keep >= 0)
            {
                shownName = Name(ps[keep]);
                if (ps.Length == 1) return null;                              // nothing else to turn off
                var path = ps[keep];
                if (path.s.m != INVALID && path.s.m < ms.Length && ms[path.s.m].type == 1) { ms[path.s.m].x = 0; ms[path.s.m].y = 0; }
                int rc = SetDisplayConfig(1, new[] { path }, (uint)ms.Length, ms, SDC_APPLY | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES | SDC_SAVE);
                if (rc == 0) return null;
                // fall through and let Windows pick the mode instead
            }
        }

        // Not active (or that failed): pick any path to it and let Windows choose the mode.
        if (!Query(QDC_ALL_PATHS, out ps, out ms)) return "can't read the display configuration";
        foreach (var p in ps)
        {
            if (p.t.av == 0 || !Matches(p, nameContains)) continue;
            var path = p;
            path.f = 1;                                                      // DISPLAYCONFIG_PATH_ACTIVE
            path.s.m = INVALID;
            path.t.m = INVALID;
            shownName = Name(p);
            int rc = SetDisplayConfig(1, new[] { path }, 0, null, SDC_APPLY | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES | SDC_SAVE);
            return rc == 0 ? null : "SetDisplayConfig rc=" + rc;
        }
        return nameContains + " is not connected";
    }
}
