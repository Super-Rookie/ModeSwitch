// HueMenu.cs - the tray menu's "Lights" submenu: brightness, on/off, the room's Hue scenes and each
// light of hue.tv.group with its colours. The state is read in the background when the submenu opens.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

sealed class HueMenu
{
    readonly Func<string> ip, key, groupName;
    readonly Control ui;                      // marshals results back to the UI thread

    // All bridge calls run on one worker thread, in order, so slider moves never overlap.
    readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();

    // Controls of the submenu currently built (the tray menu is rebuilt every time it opens).
    ToolStripMenuItem root, header, power;
    TrackBar slider;
    Label value;
    readonly List<ToolStripItem> listed = new List<ToolStripItem>();   // scenes and lights, once read
    bool loading;                             // populating the controls: don't send anything back
    string group;                             // the bridge's id for hue.tv.group, once read
    bool anyOn, ready;
    string[] lit = new string[0];             // the lights that are on: the slider changes only these

    public HueMenu(Func<string> ip, Func<string> key, Func<string> groupName, Control ui)
    {
        this.ip = ip; this.key = key; this.groupName = groupName; this.ui = ui;
        var worker = new Thread(() =>
        {
            foreach (Action job in jobs.GetConsumingEnumerable())
            {
                try { job(); } catch { }
            }
        }) { IsBackground = true, Name = "HueMenu" };
        worker.Start();
    }

    void Run(Action job) { jobs.Add(job); }

    void Ui(Action a)
    {
        try { ui.BeginInvoke(a); } catch { }
    }

    public ToolStripMenuItem Build()
    {
        root = new ToolStripMenuItem("Lights");
        var items = root.DropDownItems;
        listed.Clear();
        group = null;

        header = RoomSoundMenu.Header("Lights: reading...");
        items.Add(header);
        items.Add(RoomSoundMenu.SliderRow(out slider, out value, 1, 100));
        power = new ToolStripMenuItem("On", null, (s, e) =>
        {
            bool on = !anyOn;
            string g = group;
            Run(() => Result(Hue.SetGroup(ip(), key(), g, on), "on/off"));
        });
        items.Add(power);

        string problem = ip().Length == 0 ? "Lights: not configured (hue.ip)"
                       : groupName().Length == 0 ? "Lights: not configured (hue.tv.group)"
                       : key() == null ? "Lights: pair the bridge first (Room setup)"
                       : null;
        SetEnabled(false, problem ?? "Lights: reading...");
        slider.ValueChanged += (s, e) => { value.Text = slider.Value + "%"; if (!loading) QueueBrightness(); };
        ready = problem == null;
        if (ready) root.DropDownOpening += (s, e) => Run(Read);
        return root;
    }

    // For a shortcut: reads what the submenu shows when it opens, then calls done on the UI thread.
    public void Load(Action done)
    {
        if (ready) Run(Read);
        Run(() => Ui(done));
    }

    void SetEnabled(bool on, string status)
    {
        header.Text = status;
        slider.Enabled = power.Enabled = on;
        if (!on) value.Text = "";
    }

    // ---- worker thread ----
    void Read()
    {
        string name = groupName(), g;
        List<HueLight> lights;
        List<KeyValuePair<string, string>> scenes;
        string err = Hue.ReadGroup(ip(), key(), name, out g, out lights, out scenes);
        Ui(() =>
        {
            if (header == null) return;
            if (err != null) { SetEnabled(false, "Lights: " + err); return; }
            group = g;
            anyOn = false;
            int bri = 0;
            var on = new List<string>();
            foreach (HueLight l in lights)
            {
                anyOn |= l.On;
                if (l.On && l.Reachable) { bri += l.Brightness; on.Add(l.Id); }
            }
            lit = on.ToArray();
            loading = true;
            slider.Value = lit.Length > 0 ? Math.Max(1, Math.Min(100, (int)Math.Round(bri / (double)lit.Length * 100 / 254))) : 100;
            value.Text = slider.Value + "%";
            loading = false;
            power.Checked = anyOn;
            SetEnabled(true, name);
            if (lit.Length == 0)                      // nothing on: the slider has nothing to change
            {
                slider.Enabled = false;
                value.Text = "";
            }
            ShowLists(lights, scenes);
        });
    }

    void ShowLists(List<HueLight> lights, List<KeyValuePair<string, string>> scenes)
    {
        foreach (ToolStripItem old in listed) root.DropDownItems.Remove(old);
        listed.Clear();
        string g = group;
        if (scenes.Count > 0)
        {
            var sceneMenu = new ToolStripMenuItem("Scenes");
            foreach (var scene in scenes)
            {
                string id = scene.Key;
                sceneMenu.DropDownItems.Add(new ToolStripMenuItem(scene.Value, null, (s, e) => Run(() => Result(Hue.RecallScene(ip(), key(), g, id), "scene"))));
            }
            Add(sceneMenu);
        }
        if (lights.Count > 0)
        {
            Add(RoomSoundMenu.Header("Lights:"));
            foreach (HueLight light in lights) Add(LightMenu(light));
        }
    }

    // One light's own submenu: on/off, and whichever colours the light can show.
    ToolStripMenuItem LightMenu(HueLight l)
    {
        var item = new ToolStripMenuItem(l.Reachable ? l.Name : l.Name + " (no power)") { Checked = l.On, Enabled = l.Reachable };
        var sub = item.DropDownItems;
        bool on = !l.On;
        sub.Add(new ToolStripMenuItem("On", null, (s, e) => Run(() => Result(Hue.SetLight(ip(), key(), l.Id, on), l.Name))) { Checked = l.On });
        if (l.HasColour)
            sub.Add(new ToolStripMenuItem("Colour...", null, (s, e) => Ui(() => PickColour(l))));
        if (l.HasWhite)
            for (int i = 0; i < Whites.GetLength(0); i++)
            {
                int mired = (int)Whites[i, 1];
                sub.Add(new ToolStripMenuItem((string)Whites[i, 0], null, (s, e) => Run(() => Result(Hue.SetLightWhite(ip(), key(), l.Id, mired), l.Name))));
            }
        if (!l.HasColour && !l.HasWhite)
            sub.Add(RoomSoundMenu.Header("No colour on this light"));
        return item;
    }

    static readonly object[,] Whites = { { "Warm white", 370 }, { "Neutral white", 250 }, { "Cool white", 153 } };

    void PickColour(HueLight l)
    {
        int r, g, b;
        Hue.XyToRgb(l.X, l.Y, out r, out g, out b);
        using (var dialog = new ColorDialog { FullOpen = true, Color = Color.FromArgb(r, g, b) })
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1) })
        {
            owner.Show();                             // a tray app has no window, so the dialog needs one to stay in front
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            Color c = dialog.Color;
            Run(() => Result(Hue.SetLightColour(ip(), key(), l.Id, c.R, c.G, c.B), l.Name));
        }
    }

    void Add(ToolStripItem item)
    {
        listed.Add(item);
        root.DropDownItems.Add(item);
    }

    void Result(string err, string what)
    {
        if (err == null) return;
        string msg = "Lights: " + what + " failed - " + err;
        Ui(() => { if (header != null) header.Text = msg; });
    }

    // Slider moves are coalesced: only the latest position is sent once the worker gets to it. The
    // pause keeps to the rate of commands the bridge can pass on to the lights.
    volatile bool queued;
    volatile int wanted;

    void QueueBrightness()
    {
        wanted = slider.Value;
        if (queued) return;
        queued = true;
        string[] lights = lit;
        Run(() =>
        {
            queued = false;
            int percent = wanted;
            foreach (string light in lights) Result(Hue.SetLightBrightness(ip(), key(), light, percent), "brightness");
            Thread.Sleep(250);
        });
    }
}
