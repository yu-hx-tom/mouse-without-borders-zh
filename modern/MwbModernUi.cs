// Native presentation layer for Mouse without Borders 2.2.1.0327.
// Existing controls and their event handlers remain the source of truth.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MwbModernUi
{
    public static class Style
    {
        static readonly Color Canvas = Color.FromArgb(247,248,245);
        static readonly Color Sidebar = Color.FromArgb(236,241,234);
        static readonly Color Ink = Color.FromArgb(32,53,43);
        static readonly Color Muted = Color.FromArgb(108,122,113);
        static readonly Color Green = Color.FromArgb(40,92,67);
        static readonly Color Line = Color.FromArgb(220,228,219);
        // Pixel fonts and logical bounds share one explicit DPI scale. Point fonts
        // otherwise grow independently of this legacy application's manual layout.
        static readonly Font Body = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font Small = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font Heading = new Font("Microsoft YaHei UI", 32f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly Font CardHeading = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly ConditionalWeakTable<Form, Shell> Shells = new ConditionalWeakTable<Form, Shell>();
        static readonly ConditionalWeakTable<Control, object> SkinnedMachines = new ConditionalWeakTable<Control, object>();
        static readonly string[] Titles = { "设备与连接", "键鼠与共享", "快捷键", "网络与高级" };
        static readonly string[] Descriptions = {
            "一套键鼠，让桌面之间的切换自然发生。",
            "按你的习惯，调整跨屏、剪贴板和键盘体验。",
            "把常用动作交给键盘，让操作少一步。",
            "为无法通过电脑名称找到的设备，指定连接地址。" };

        sealed class Shell
        {
            public Form Form;
            public Panel Side, Host, Footer;
            public TabControl Tabs;
            public TabPage[] Pages;
            public Label Title, Description, Edition, Footnote;
            public Button[] Nav = new Button[4];
            public bool LayingOut;
            public int Page;
            public int Dpi = 96;
            public DpiWindow Window;
            public Dictionary<Control, FontSpec> Fonts = new Dictionary<Control, FontSpec>();
        }
        sealed class FontSpec
        {
            public string Family;
            public float Pixels;
            public FontStyle Style;
            public Font Owned;
            public int Dpi;
        }
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        sealed class DpiWindow : NativeWindow
        {
            readonly Shell shell;
            public DpiWindow(Shell value) { shell=value; AssignHandle(value.Form.Handle); }
            protected override void WndProc(ref Message m)
            {
                if(m.Msg==0x02E0 && m.LParam!=IntPtr.Zero)
                {
                    NativeRect r=(NativeRect)Marshal.PtrToStructure(m.LParam,typeof(NativeRect));
                    SetDpi(shell,(int)(m.WParam.ToInt64() & 0xffff));
                    Rectangle bounds=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
                    Rectangle work=Screen.FromRectangle(bounds).WorkingArea;
                    shell.Form.MinimumSize=new Size(Math.Min(Px(shell.Form,960),work.Width),Math.Min(Px(shell.Form,520),work.Height));
                    shell.Form.Bounds=bounds;
                    Relayout(shell.Form); m.Result=IntPtr.Zero; return;
                }
                base.WndProc(ref m);
            }
        }
        static int DpiOf(Control c)
        {
            Shell s; Form f=c as Form ?? c.FindForm();
            return f!=null && Shells.TryGetValue(f,out s)?s.Dpi:96;
        }
        static int Px(Control c,int value) { return (int)Math.Round(value*DpiOf(c)/96.0); }
        static int Logical(Control c,int value) { return (int)Math.Round(value*96.0/DpiOf(c)); }
        static void ScaleFonts(Shell s,Control c)
        {
            // Capture inherited fonts before changing any parent font, including
            // when the very first settings window opens on a high-DPI monitor.
            RememberFonts(s,c);
            ApplyFonts(s,c);
        }
        static void RememberFonts(Shell s,Control c)
        {
            FontSpec spec;
            if(!s.Fonts.TryGetValue(c,out spec))
            {
                spec=new FontSpec { Family=c.Font.FontFamily.Name,Pixels=c.Font.Unit==GraphicsUnit.Pixel?c.Font.Size:c.Font.SizeInPoints*96f/72f,Style=c.Font.Style };
                s.Fonts.Add(c,spec);
            }
            foreach(Control child in c.Controls) RememberFonts(s,child);
        }
        static void ApplyFonts(Shell s,Control c)
        {
            FontSpec spec=s.Fonts[c];
            if(spec.Dpi!=s.Dpi)
            {
                Font old=spec.Owned;
                spec.Owned=new Font(spec.Family,spec.Pixels*s.Dpi/96f,spec.Style,GraphicsUnit.Pixel);
                spec.Dpi=s.Dpi; c.Font=spec.Owned;
                if(old!=null) old.Dispose();
            }
            ComboBox combo=c as ComboBox;
            if(combo!=null) combo.ItemHeight=Px(c,24);
            foreach(Control child in c.Controls) ApplyFonts(s,child);
        }
        static void SetDpi(Shell s,int dpi)
        {
            s.Dpi=Math.Max(96,Math.Min(768,dpi));
            ScaleFonts(s,s.Form);
            foreach(Button b in s.Nav) if(b!=null) b.Padding=new Padding(Px(b,16),0,0,0);
            Rectangle work=Screen.FromControl(s.Form).WorkingArea;
            s.Form.MinimumSize=new Size(Math.Min(Px(s.Form,960),work.Width),Math.Min(Px(s.Form,520),work.Height));
        }
        static object Field(object owner, string name)
        {
            FieldInfo f = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f == null ? null : f.GetValue(owner);
        }
        static Control C(object owner, string name) { return Field(owner, name) as Control; }
        static void Box(Control c, int x, int y, int w, int h)
        {
            if (c == null) return;
            x=Px(c,x); y=Px(c,y); w=Px(c,w); h=Px(c,h);
            ScrollableControl scrolling=c.Parent as ScrollableControl;
            if(scrolling!=null && scrolling.AutoScroll) { x+=scrolling.AutoScrollPosition.X; y+=scrolling.AutoScrollPosition.Y; }
            c.Dock = DockStyle.None; c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            c.AutoSize = false; c.SetBounds(x, y, Math.Max(1,w), Math.Max(1,h));
        }
        static Label Label(Control parent, string name, string text, Font font, Color color)
        {
            Label l = new Label { Name=name, Text=text, Font=font, ForeColor=color, BackColor=Color.Transparent, AutoSize=false };
            parent.Controls.Add(l); return l;
        }
        static Control Named(Control parent, string name) { return parent.Controls[name]; }
        static void BaseStyle(Control root)
        {
            root.Font = Body; root.ForeColor = Ink;
            foreach (Control c in root.Controls) BaseStyle(c);
            LinkLabel link = root as LinkLabel;
            if (link != null) { link.LinkColor=Green; link.ActiveLinkColor=Ink; link.VisitedLinkColor=Green; link.LinkBehavior=LinkBehavior.HoverUnderline; }
            TextBox tb = root as TextBox;
            if (tb != null) { tb.BorderStyle=BorderStyle.FixedSingle; }
            ComboBox combo = root as ComboBox;
            if (combo != null)
            {
                combo.FlatStyle=FlatStyle.Standard; combo.BackColor=Color.FromArgb(246,249,242);
                combo.SelectionStart=0; combo.SelectionLength=0;
                // Paint a translated caption without changing tokens parsed by the original handlers.
                combo.DrawMode=DrawMode.OwnerDrawFixed; combo.ItemHeight=24;
                combo.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                    if(e.Index < 0) return;
                    e.DrawBackground(); string text=Convert.ToString(combo.Items[e.Index]);
                    if(text=="Disable") text="禁用"; else if(text=="Enable") text="启用"; else if(text=="Ctrl*3") text="Ctrl × 3";
                    else if(text=="All") text="所有电脑"; else if(text=="Myself") text="仅本机";
                    TextRenderer.DrawText(e.Graphics,text,combo.Font,e.Bounds,e.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
                    e.DrawFocusRectangle();
                };
            }
        }
        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d=radius*2; GraphicsPath p=new GraphicsPath();
            p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
            p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
        }
        static void Card(Control c, string title, string subtitle)
        {
            c.Text=""; c.BackColor=Color.White; c.Dock=DockStyle.None; c.Padding=Padding.Empty;
            c.Paint += delegate(object sender, PaintEventArgs e) {
                e.Graphics.Clear(Canvas); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                using(GraphicsPath p=Rounded(new Rectangle(0,0,c.Width-1,c.Height-1),Px(c,12)))
                using(Brush b=new SolidBrush(Color.White)) using(Pen pen=new Pen(Line)) { e.Graphics.FillPath(b,p); e.Graphics.DrawPath(pen,p); }
            };
            Label(c,"modernCardTitle",title,CardHeading,Ink);
            Label(c,"modernCardSubtitle",subtitle,Small,Muted);
        }
        static void CardLabels(Control card)
        {
            Box(Named(card,"modernCardTitle"),24,22,Logical(card,card.Width)-48,28);
            Box(Named(card,"modernCardSubtitle"),24,55,Logical(card,card.Width)-48,35);
        }
        static void ButtonStyle(Button b, bool primary)
        {
            b.FlatStyle=FlatStyle.Flat; b.FlatAppearance.BorderSize=primary?0:1; b.FlatAppearance.BorderColor=Line;
            b.UseVisualStyleBackColor=false; b.BackColor=primary?Green:Color.White; b.ForeColor=primary?Color.White:Ink;
            b.Cursor=Cursors.Hand; b.Font=Body;
        }

        public static void Apply(Form form)
        {
            Shell existing; if(Shells.TryGetValue(form,out existing)) return;
            form.SuspendLayout();
            // The legacy designer enables TopMost. Settings should use ordinary
            // window stacking so clicking another application can cover them.
            form.TopMost=false;
            form.AutoScaleMode=AutoScaleMode.None; BaseStyle(form); form.BackColor=Canvas;
            form.FormBorderStyle=FormBorderStyle.Sizable; form.MaximizeBox=true; form.StartPosition=FormStartPosition.CenterScreen;
            form.MinimumSize=new Size(960,680); form.MaximumSize=Size.Empty;
            form.Text="无界鼠标 · Mouse without Borders";
            Shell s=new Shell { Form=form, Tabs=(TabControl)C(form,"tabControlSetting") };
            Shells.Add(form,s);
            s.Side=new Panel { Name="modernSidebar", BackColor=Sidebar, AutoScroll=true };
            s.Host=new Panel { Name="modernPageHost", BackColor=Canvas };
            s.Footer=new Panel { Name="modernFooter", BackColor=Canvas };
            form.Controls.Add(s.Side); form.Controls.Add(s.Host); form.Controls.Add(s.Footer);
            s.Tabs.Parent=s.Host; s.Tabs.Dock=DockStyle.None; s.Tabs.Padding=new Point(0,0);
            TabPage shortcuts=new TabPage("快捷键") { Name="modernShortcuts" };
            C(form,"groupBoxShortcuts").Parent=shortcuts;
            s.Pages=new TabPage[] { (TabPage)C(form,"tabPageMain"),(TabPage)C(form,"tabPageOther"),shortcuts,(TabPage)C(form,"tabPageAdvancedSettings") };
            s.Tabs.TabPages.Clear(); s.Tabs.TabPages.AddRange(s.Pages);
            foreach(TabPage page in s.Pages) { page.UseVisualStyleBackColor=false; page.BackColor=Canvas; page.Padding=Padding.Empty; page.AutoScroll=true; }
            s.Title=Label(form,"modernTitle",Titles[0],Heading,Ink);
            s.Description=Label(form,"modernDescription",Descriptions[0],Body,Muted);
            Label brand=Label(s.Side,"modernBrand","无界鼠标",new Font("Microsoft YaHei UI",23f,FontStyle.Bold,GraphicsUnit.Pixel),Ink);
            Box(brand,28,68,170,40);
            Box(Label(s.Side,"modernWordmark","MOUSE WITHOUT BORDERS",new Font("Segoe UI",10f,FontStyle.Bold,GraphicsUnit.Pixel),Muted),29,109,170,22);
            Panel mark=new Panel { Name="modernMark", BackColor=Sidebar }; s.Side.Controls.Add(mark); Box(mark,28,28,48,30);
            mark.Paint += delegate(object sender,PaintEventArgs e) {
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                e.Graphics.ScaleTransform(DpiOf(mark)/96f,DpiOf(mark)/96f);
                using(Pen p=new Pen(Green,2)) { e.Graphics.DrawRectangle(p,1,2,23,16); e.Graphics.DrawLine(p,8,23,20,23); e.Graphics.DrawLine(p,14,19,14,23); e.Graphics.DrawRectangle(p,22,9,23,16); }
            };
            for(int i=0;i<4;i++)
            {
                int pageIndex=i; Button b=new Button { Name="modernNav"+i,Text="0"+(i+1)+"    "+Titles[i],Tag=s.Pages[i],TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(16,0,0,0),TabIndex=i };
                ButtonStyle(b,false); b.FlatAppearance.BorderSize=0;
                b.Click += delegate { s.Tabs.SelectedTab=s.Pages[pageIndex]; };
                s.Side.Controls.Add(b); s.Nav[i]=b; Box(b,16,184+i*56,180,46);
            }
            s.Edition=Label(s.Side,"modernEdition","DESKTOP EDITION\n2.2.1.0327 · 中文界面",Small,Muted);
            foreach(string name in new[]{"linkLabelHelp","linkLabelMiniLog"}) C(form,name).Parent=s.Side;
            C(form,"linkLabelHelp").Text="使用帮助  ↗"; C(form,"linkLabelMiniLog").Text="连接诊断";
            C(form,"pictureBoxMouseWithoutBorders0").Visible=false;
            C(form,"pictureBoxMouseWithoutBorders").Visible=false;
            s.Footnote=Label(s.Footer,"modernFootnote","排列与密钥调整后，点击应用更改。",Small,Muted);
            Button ok=(Button)C(form,"buttonOK"), cancel=(Button)C(form,"buttonCancel");
            ok.Parent=s.Footer; cancel.Parent=s.Footer; ok.Text="应用更改"; cancel.Text="关闭";
            ButtonStyle(ok,true); ButtonStyle(cancel,false);
            ButtonStyle((Button)C(form,"buttonNewKey"),false); C(form,"buttonNewKey").Text="生成新密钥";
            C(form,"checkBoxShowKey").Text="显示密钥"; C(form,"checkBoxTwoRow").Text="双排布局";
            C(form,"linkLabelReConfigure").Text="重新配对设备  ↗";
            Card(C(form,"groupBoxMachineMatrix"),"你的桌面，连在一起","拖动电脑图标，使排列与你桌面上的屏幕位置一致。勾选空位后可填写电脑名。");
            Card(C(form,"groupBoxKeySetup"),"安全连接","两台电脑使用相同的密钥，即可建立连接。请仅与自己的设备共享。");
            C(form,"LabelEnc").Text="共享密钥";
            Card(C(form,"groupBoxOtherOptions"),"按你的方式使用","偏好设置会自动保存，调整后即生效。");
            Card(C(form,"groupBoxShortcuts"),"指尖上的控制","保留熟悉的组合键，也可以为常用动作选择新的按键。");
            Card(C(form,"groupBoxDNS"),"电脑名称与 IP 映射","每行输入一个电脑名称和对应的 IP 地址，用空格分隔。");
            TextBox dns=(TextBox)C(form,"textBoxDNS"); dns.BorderStyle=BorderStyle.None; dns.BackColor=Canvas; dns.ForeColor=Muted; dns.Font=Body; dns.TabStop=false;
            TextBox map=(TextBox)C(form,"textBoxMachineName2IP"); map.Font=new Font("Consolas",15f,FontStyle.Regular,GraphicsUnit.Pixel); map.BackColor=Color.FromArgb(251,252,249); map.ScrollBars=ScrollBars.Vertical;
            AddOptionDescriptions(form);
            Array initialMachines=Field(form,"machines") as Array;
            if(initialMachines!=null) foreach(Control machine in initialMachines) if(machine!=null) ApplyMachine(machine);
            s.Tabs.SelectedIndexChanged += delegate { UpdatePage(s); };
            form.Resize += delegate { Relayout(form); };
            form.Shown += delegate { Relayout(form); };
            // Reuse the legacy UI thread's awareness for every HWND. Switching only
            // the parent to PMv2 makes Windows reject parenting existing child HWNDs.
            IntPtr handle=form.Handle;
            int dpi=96;
            try { dpi=(int)GetDpiForWindow(handle); } catch(EntryPointNotFoundException) { using(Graphics g=form.CreateGraphics()) dpi=(int)g.DpiX; }
            SetDpi(s,dpi);
            s.Window=new DpiWindow(s);
            form.HandleDestroyed += delegate { if(s.Window!=null) s.Window.ReleaseHandle(); };
            form.HandleCreated += delegate { s.Window=new DpiWindow(s); };
            form.Disposed += delegate { foreach(FontSpec spec in s.Fonts.Values) if(spec.Owned!=null) spec.Owned.Dispose(); s.Fonts.Clear(); };
            Rectangle work=Screen.FromControl(form).WorkingArea;
            form.ClientSize=new Size(Math.Min(Px(form,1140),work.Width-Px(form,32)),Math.Min(Px(form,800),work.Height-Px(form,64)));
            UpdatePage(s); form.ResumeLayout(false); Relayout(form);
        }

        static readonly string[] Options = {
            "checkBoxShareClipboard","checkBoxTransferFile","checkBoxSameSubNet","checkBoxReserseLookup","checkBoxClipNetStatus","checkBoxSendLog","checkBoxBlockScreenSaver","checkBoxDisableCAD",
            "checkBoxCircle","checkBoxMouseMoveRelatively","checkBoxBlockMouseAtCorners","checkBoxHideMouse","checkBoxDrawMouse","checkBoxVKMap","checkBoxHideLogo" };
        static readonly string[] OptionNotes = {
            "在电脑之间复制与粘贴","通过共享剪贴板传送文件","只与当前子网中的电脑连接","连接时校验电脑地址","显示共享状态的提示信息","帮助原程序排查错误","连接期间保持其他电脑唤醒","控制 Windows 登录组合键要求",
            "从最外侧屏幕回到另一端","使用相对位移移动鼠标","减少经过屏幕角落时的误切换","切换电脑时隐藏边缘指针","显示软件绘制的鼠标指针","适配不同电脑的键盘布局","隐藏原程序在登录界面的标志" };
        static void AddOptionDescriptions(Form form)
        {
            Control group=C(form,"groupBoxOtherOptions");
            for(int i=0;i<Options.Length;i++) Label(group,"modernOption"+i,OptionNotes[i],Small,Muted);
            // This legacy build deliberately hides error-log sending; hide its companion copy as well.
            Named(group,"modernOption5").Visible=false;
        }
        static void UpdatePage(Shell s)
        {
            int page=Array.IndexOf(s.Pages,s.Tabs.SelectedTab); if(page<0) page=0; s.Page=page;
            s.Title.Text=Titles[page]; s.Description.Text=Descriptions[page];
            for(int i=0;i<4;i++) { s.Nav[i].BackColor=i==page?Color.FromArgb(216,228,214):Sidebar; s.Nav[i].ForeColor=i==page?Green:Muted; }
            C(s.Form,"buttonOK").Visible=page==0;
            s.Footnote.Text=page==0?"排列与密钥调整后，点击应用更改。":"本页设置会自动保存。";
            Relayout(s.Form);
        }
        public static void Relayout(Form form)
        {
            Shell s; if(!Shells.TryGetValue(form,out s) || s.LayingOut) return;
            s.LayingOut=true;
            try
            {
                int w=Logical(form,form.ClientSize.Width),h=Logical(form,form.ClientSize.Height),side=212,x=side+30,bodyW=w-x-30;
                Box(s.Side,0,0,side,h); Box(s.Title,x,38,bodyW,46); Box(s.Description,x,94,bodyW,30);
                s.Side.Height=form.ClientSize.Height;
                Box(s.Host,x,150,bodyW,h-230); Box(s.Footer,x,h-68,bodyW,54);
                // The native page control stays intact; its headers are clipped by this host.
                s.Tabs.SetBounds(0,0,s.Host.Width,s.Host.Height);
                Rectangle display=s.Tabs.DisplayRectangle;
                s.Tabs.SetBounds(-display.X,-display.Y,s.Host.Width+s.Tabs.Width-display.Width,s.Host.Height+s.Tabs.Height-display.Height);
                Box(Named(s.Side,"modernBrand"),28,68,170,40);
                Box(Named(s.Side,"modernWordmark"),29,109,170,22);
                Box(Named(s.Side,"modernMark"),28,28,48,30);
                for(int i=0;i<4;i++) Box(s.Nav[i],16,158+i*48,180,42);
                int helpY=Math.Max(368,h-164);
                Box(s.Edition,28,helpY+78,170,55);
                Box(C(form,"linkLabelHelp"),28,helpY,170,26); Box(C(form,"linkLabelMiniLog"),28,helpY+33,170,26);
                Box(s.Footnote,0,12,Math.Max(220,bodyW-266),32);
                Box(C(form,"buttonOK"),bodyW-232,2,132,42); Box(C(form,"buttonCancel"),bodyW-88,2,88,42);
                int cw=bodyW-22;
                bool two=((CheckBox)C(form,"checkBoxTwoRow")).Checked;
                Control matrix=C(form,"groupBoxMachineMatrix"),key=C(form,"groupBoxKeySetup");
                int matrixH=two?526:344;
                Box(matrix,0,0,cw,matrixH); CardLabels(matrix);
                Box(C(form,"checkBoxTwoRow"),24,matrixH-47,150,26);
                Box(C(form,"linkLabelReConfigure"),cw-186,matrixH-47,163,26);
                Box(key,0,matrixH+18,cw,202); CardLabels(key);
                Box(C(form,"LabelEnc"),24,100,100,26);
                Box(C(form,"textBoxEnc"),24,132,cw-290,32);
                Box(C(form,"checkBoxShowKey"),cw-246,134,104,26); Box(C(form,"buttonNewKey"),cw-140,126,116,40);
                Control options=C(form,"groupBoxOtherOptions"); Box(options,0,0,cw,516); CardLabels(options);
                int col=(cw-60)/2;
                for(int i=0;i<Options.Length;i++)
                {
                    if(i==5) continue;
                    int n=i<8?(i>5?i-1:i):i-8,xx=i<8?24:36+col,yy=99+n*56;
                    Box(C(form,Options[i]),xx,yy,col,25); Box(Named(options,"modernOption"+i),xx+20,yy+26,col-20,22);
                }
                LayoutShortcuts(form,cw);
                Box(C(form,"textBoxDNS"),4,6,cw-8,82);
                Control network=C(form,"groupBoxDNS"); Box(network,0,106,cw,400); CardLabels(network);
                Box(C(form,"textBoxMachineName2IP"),24,106,cw-48,264);
                Arrange(form);
            }
            finally { s.LayingOut=false; }
        }
        static void LayoutShortcuts(Form form,int w)
        {
            Control group=C(form,"groupBoxShortcuts"); Box(group,0,0,w,602); CardLabels(group);
            Box(C(form,"labelSwitchBetweenMachine"),24,108,w-48,25);
            Box(C(form,"radioButtonF1"),24,147,174,28); Box(C(form,"radioButtonNum"),210,147,145,28); Box(C(form,"radioButtonDisable"),370,147,130,28);
            string[] labels={"labelShowSettings","labelLockMachine","labelReconnect","labelScreenCapture","labelExitMM","labelSwitch2ALLPCMode","labelEasyMouse","LabelToggleEasyMouse"};
            string[] combos={"comboBoxShowSettings","comboBoxLockMachine","comboBoxReconnect","comboBoxScreenCapture","comboBoxExitMM","comboBoxSwitchToALLPC","comboBoxEasyMouseOption","comboBoxEasyMouse"};
            for(int i=0;i<labels.Length;i++) { int yy=203+i*47; Box(C(form,labels[i]),24,yy,w-218,28); Box(C(form,combos[i]),w-174,yy-2,150,32); }
        }
        public static void Arrange(Form form)
        {
            Shell s; if(!Shells.TryGetValue(form,out s)) return;
            Control group=C(form,"groupBoxMachineMatrix"); bool two=((CheckBox)C(form,"checkBoxTwoRow")).Checked;
            int desired=Px(group,two?526:344);
            if(group.Height!=desired && !s.LayingOut) { Relayout(form); return; }
            Array machines=Field(form,"machines") as Array; if(machines==null) return;
            int groupWidth=Logical(group,group.Width),width=(groupWidth-48-36)/4;
            int n=0; foreach(object value in machines)
            {
                Control machine=value as Control; if(machine==null) { n++; continue; }
                ApplyMachine(machine);
                ScaleFonts(s,machine);
                int xx=two?(groupWidth-(width*2+16))/2+(n%2)*(width+16):24+n*(width+12);
                int yy=100+(two?n/2*190:0);
                Box(machine,xx,yy,width,180); LayoutMachine(machine); machine.Visible=true; n++;
            }
        }
        public static void ApplyMachine(Control machine)
        {
            object marker; if(SkinnedMachines.TryGetValue(machine,out marker)) return;
            SkinnedMachines.Add(machine,new object());
            ContainerControl container=machine as ContainerControl;
            if(container!=null) container.AutoScaleMode=AutoScaleMode.None;
            machine.Font=Body; machine.BackColor=Color.White;
            machine.Paint += delegate(object sender,PaintEventArgs e) {
                bool enabled=((CheckBox)C(machine,"checkBoxEnabled")).Checked;
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                using(GraphicsPath p=Rounded(new Rectangle(0,0,machine.Width-1,machine.Height-1),Px(machine,10)))
                using(Brush b=new SolidBrush(enabled?Color.FromArgb(240,246,237):Color.FromArgb(250,251,248)))
                using(Pen pen=new Pen(enabled?Color.FromArgb(185,206,179):Line)) { e.Graphics.FillPath(b,p); e.Graphics.DrawPath(pen,p); }
            };
            PictureBox logo=(PictureBox)C(machine,"pictureBoxLogo"); logo.BackColor=Color.Transparent;
            logo.Paint += delegate(object sender,PaintEventArgs e) {
                bool enabled=((CheckBox)C(machine,"checkBoxEnabled")).Checked;
                e.Graphics.Clear(enabled?Color.FromArgb(240,246,237):Color.FromArgb(250,251,248));
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                e.Graphics.ScaleTransform(DpiOf(machine)/96f,DpiOf(machine)/96f);
                int x=(Logical(logo,logo.Width)-62)/2,y=0;
                using(Pen p=new Pen(enabled?Green:Color.FromArgb(157,172,157),2))
                using(Brush b=new SolidBrush(enabled?Color.FromArgb(218,233,210):Color.FromArgb(235,240,231)))
                { e.Graphics.FillRectangle(b,x+3,y+3,56,32); e.Graphics.DrawRectangle(p,x,y,62,38); e.Graphics.DrawLine(p,x+31,y+39,x+31,y+48); e.Graphics.DrawLine(p,x+18,y+49,x+44,y+49); }
            };
            CheckBox check=(CheckBox)C(machine,"checkBoxEnabled"); check.BackColor=Color.Transparent;
            check.CheckedChanged += delegate { machine.Invalidate(true); };
            foreach(string name in new[]{"labelStatusClient","labelStatusServer"}) { Control c=C(machine,name); c.Font=Small; c.ForeColor=Muted; c.BackColor=Color.Transparent; }
            TextBox tb=(TextBox)C(machine,"textBoxName"); tb.Font=Small; tb.BorderStyle=BorderStyle.FixedSingle; tb.TextAlign=HorizontalAlignment.Center;
            machine.Resize += delegate { LayoutMachine(machine); };
            machine.Size=new Size(180,180); LayoutMachine(machine);
        }
        static void LayoutMachine(Control machine)
        {
            int w=Logical(machine,machine.Width);
            Box(C(machine,"pictureBoxLogo"),8,33,w-16,52);
            Box(C(machine,"checkBoxEnabled"),w-25,10,19,19); C(machine,"checkBoxEnabled").BringToFront();
            Box(C(machine,"textBoxName"),9,95,w-18,27);
            Box(C(machine,"labelStatusClient"),8,126,w-16,21); Box(C(machine,"labelStatusServer"),8,150,w-16,21);
        }
    }
}
