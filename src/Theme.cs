using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoCat
{
    static class Skin
    {
        public static readonly Color Background=Color.FromArgb(15,16,19),Panel=Color.FromArgb(22,23,27),Border=Color.FromArgb(58,58,64),Text=Color.FromArgb(202,201,206),Muted=Color.FromArgb(128,126,135),Accent=Color.FromArgb(224,161,137);
        public static readonly Font Font=new Font("Tahoma",11,FontStyle.Regular,GraphicsUnit.Pixel),Logo=new Font("Consolas",18,FontStyle.Bold,GraphicsUnit.Pixel);
        public static bool Streamproof=false;
        
        public static int D(Graphics g,float value){return (int)Math.Round(value);}
        public static void TextAt(Graphics g,string text,Rectangle rect,Color color,TextFormatFlags flags=TextFormatFlags.Left|TextFormatFlags.VerticalCenter)
        {TextRenderer.DrawText(g,text,Font,rect,color,flags|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);}
        public static void Protect(IntPtr handle){}
    }
    sealed class FieldGroup:Panel
    {
        public FieldGroup(string title,int x,int y,int width,int height){Text=title;SetBounds(x,y,width,height);BackColor=Skin.Panel;DoubleBuffered=true;}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;int y=Skin.D(g,7),x=Skin.D(g,11);
            using(var pen=new Pen(Color.Black))g.DrawRectangle(pen,0,y,Width-1,Height-y-1);
            using(var pen=new Pen(Skin.Border))g.DrawRectangle(pen,1,y+1,Width-3,Height-y-3);
            int width=TextRenderer.MeasureText(Text,Skin.Font).Width+Skin.D(g,6);
            using(var brush=new SolidBrush(BackColor))g.FillRectangle(brush,x,y-Skin.D(g,6),width,Skin.D(g,14));
            Skin.TextAt(g,Text,new Rectangle(x+Skin.D(g,3),0,width,Skin.D(g,15)),Skin.Text);
        }
    }
    class MicroButton:Control
    {
        bool hover,pressed;public Action Action;
        public MicroButton(string text,Action action){Text=text;Action=action;Height=23;Font=Skin.Font;TabStop=true;AccessibleRole=AccessibleRole.PushButton;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);SetStyle(ControlStyles.StandardClick|ControlStyles.StandardDoubleClick,false);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){Focus();pressed=true;Capture=true;Invalidate();}base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){bool click=pressed&&ClientRectangle.Contains(e.Location);pressed=false;Capture=false;Invalidate();if(click)OnClick(EventArgs.Empty);base.OnMouseUp(e);}
        protected override void OnClick(EventArgs e){if(Enabled&&Action!=null)Action();base.OnClick(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){OnClick(EventArgs.Empty);e.Handled=true;}base.OnKeyDown(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            Color top=pressed?Color.FromArgb(32,30,35):hover?Color.FromArgb(54,47,44):Color.FromArgb(44,44,49);
            using(var b=new LinearGradientBrush(ClientRectangle,top,Color.FromArgb(29,29,33),90))e.Graphics.FillRectangle(b,ClientRectangle);
            using(var p=new Pen(Focused?Skin.Accent:Skin.Border))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);
            Skin.TextAt(e.Graphics,Text,new Rectangle(5,0,Width-10,Height),Enabled?Skin.Text:Skin.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        }
    }
    sealed class MicroCheck:Control
    {
        readonly Func<bool> get;readonly Action<bool> set;
        public MicroCheck(string text,Func<bool> getter,Action<bool> setter){Text=text;get=getter;set=setter;Height=20;TabStop=true;AccessibleRole=AccessibleRole.CheckButton;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){Focus();set(!get());Invalidate();}base.OnMouseDown(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space){set(!get());Invalidate();e.Handled=true;}base.OnKeyDown(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;int size=Skin.D(g,10),y=(Height-size)/2;
            using(var b=new SolidBrush(Color.FromArgb(29,29,33)))g.FillRectangle(b,0,y,size,size);
            using(var p=new Pen(Focused?Skin.Accent:Skin.Border))g.DrawRectangle(p,0,y,size,size);
            if(get())using(var b=new LinearGradientBrush(new Rectangle(2,y+2,size-3,size-3),Color.FromArgb(243,201,180),Skin.Accent,90))g.FillRectangle(b,2,y+2,size-3,size-3);
            Skin.TextAt(g,Text,new Rectangle(Skin.D(g,18),0,Width-Skin.D(g,18),Height),Skin.Text);
        }
    }
    sealed class MicroSlider:Control
    {
        public Func<string> Display; public Action EditValue;
        readonly Func<double> get;readonly Action<double> set;readonly double min,max,step;readonly string format,suffix;
        bool dragging;
        public MicroSlider(string text,double min,double max,double step,string format,string suffix,Func<double> getter,Action<double> setter)
        {Text=text;this.min=min;this.max=max;this.step=step;this.format=format;this.suffix=suffix;get=getter;set=setter;Height=36;TabStop=true;AccessibleRole=AccessibleRole.Slider;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);}
        void Set(double value){set(Math.Max(min,Math.Min(max,Math.Round(value/step)*step)));Invalidate();}
        void Position(int x){int pad=(int)Math.Round(12*DeviceScale());Set(min+(max-min)*Math.Max(0,Math.Min(1,(x-pad)/(double)Math.Max(1,Width-2*pad))));}
        float DeviceScale(){return 1;}
        protected override void OnMouseDown(MouseEventArgs e)
        {if(e.Button==MouseButtons.Left){if(EditValue!=null&&e.Y<20&&e.X>=Width-170){EditValue();return;}Focus();int pad=(int)(12*DeviceScale());if(e.X<pad)Set(get()-step);else if(e.X>=Width-pad)Set(get()+step);else{dragging=true;Capture=true;Position(e.X);}}base.OnMouseDown(e);}
        protected override void OnMouseMove(MouseEventArgs e){if(dragging)Position(e.X);base.OnMouseMove(e);}
        protected override void OnMouseUp(MouseEventArgs e){dragging=false;Capture=false;base.OnMouseUp(e);}
        protected override bool IsInputKey(Keys key){return key==Keys.Left||key==Keys.Right||base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){Set(get()+(e.KeyCode==Keys.Right?step:-step));e.Handled=true;}base.OnKeyDown(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;int pad=Skin.D(g,12),barY=Skin.D(g,24),barH=Math.Max(3,Skin.D(g,4));
            Skin.TextAt(g,Text,new Rectangle(pad,0,Width-pad*2,Skin.D(g,19)),Skin.Text);
            Skin.TextAt(g,Display==null?get().ToString(format)+suffix:Display(),new Rectangle(pad,0,Width-pad*2,Skin.D(g,19)),Skin.Muted,TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
            using(var b=new SolidBrush(Color.FromArgb(12,12,15)))g.FillRectangle(b,pad,barY,Width-pad*2,barH);
            using(var b=new SolidBrush(Skin.Accent))g.FillRectangle(b,pad+1,barY+1,(int)((Width-pad*2-2)*Math.Max(0,Math.Min(1,(get()-min)/(max-min)))),Math.Max(1,barH-2));
            using(var p=new Pen(Focused?Skin.Accent:Skin.Border))g.DrawRectangle(p,pad,barY,Width-pad*2,barH);
            Skin.TextAt(g,"-",new Rectangle(0,barY-Skin.D(g,6),pad,Skin.D(g,16)),Skin.Muted);
            Skin.TextAt(g,"+",new Rectangle(Width-pad,barY-Skin.D(g,6),pad,Skin.D(g,16)),Skin.Muted,TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
        }
    }
    sealed class ChoicePopup:Form
    {
        readonly string[] values;readonly Action<int> choose;int selected;
        public ChoicePopup(string[] values,int selected,int width,Action<int> choose)
        {
            this.values=values;this.selected=selected;this.choose=choose;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;BackColor=Skin.Panel;Font=Skin.Font;DoubleBuffered=true;KeyPreview=true;AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(width,values.Length*24+2);
            Deactivate+=(a,b)=>Close();
        }
        protected override CreateParams CreateParams{get{var cp=base.CreateParams;cp.ExStyle|=0x80;return cp;}}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Skin.Protect(Handle);}
        protected override void OnPaint(PaintEventArgs e)
        {
            using(var p=new Pen(Skin.Border))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);
            for(int i=0;i<values.Length;i++)
            {
                var r=new Rectangle(1,1+i*24,Width-2,24);if(i==selected)using(var b=new SolidBrush(Color.FromArgb(53,43,39)))e.Graphics.FillRectangle(b,r);
                r.X+=9;r.Width-=15;Skin.TextAt(e.Graphics,values[i],r,i==selected?Color.White:Skin.Text);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e){selected=Math.Max(0,Math.Min(values.Length-1,(e.Y-1)/24));Invalidate();base.OnMouseMove(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left&&ClientRectangle.Contains(e.Location)){int index=Math.Max(0,Math.Min(values.Length-1,(e.Y-1)/24));choose(index);Close();}base.OnMouseDown(e);}
        protected override void OnKeyDown(KeyEventArgs e)
        {if(e.KeyCode==Keys.Escape)Close();else if(e.KeyCode==Keys.Enter){choose(selected);Close();}else if(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down){selected=(selected+values.Length+(e.KeyCode==Keys.Down?1:-1))%values.Length;Invalidate();}base.OnKeyDown(e);}
    }
    sealed class MicroSelect:Control
    {
        readonly string[] values;readonly Func<int> get;readonly Action<int> set;
        public MicroSelect(string[] values,Func<int> getter,Action<int> setter){this.values=values;get=getter;set=setter;Height=22;TabStop=true;AccessibleRole=AccessibleRole.ComboBox;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);}
        void Open()
        {
            Focus();var popup=new ChoicePopup(values,Math.Max(0,Math.Min(values.Length-1,get())),Width,i=>{set(i);Invalidate();});
            Point p=PointToScreen(new Point(0,Height+2));Rectangle screen=Screen.FromControl(this).WorkingArea;
            popup.StartPosition=FormStartPosition.Manual;popup.Location=new Point(Math.Min(p.X,screen.Right-popup.Width),p.Y+popup.Height>screen.Bottom?p.Y-Height-popup.Height-2:p.Y);popup.Show(FindForm());
        }
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)Open();base.OnMouseDown(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter||e.KeyCode==Keys.Down){Open();e.Handled=true;}base.OnKeyDown(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            using(var b=new LinearGradientBrush(ClientRectangle,Color.FromArgb(38,38,43),Color.FromArgb(28,28,32),90))e.Graphics.FillRectangle(b,ClientRectangle);
            using(var p=new Pen(Focused?Skin.Accent:Skin.Border))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);
            Skin.TextAt(e.Graphics,values[Math.Max(0,Math.Min(values.Length-1,get()))],new Rectangle(7,0,Width-25,Height),Skin.Text);
            int x=Width-11,y=Height/2;using(var b=new SolidBrush(Skin.Muted))e.Graphics.FillPolygon(b,new Point[]{new Point(x-3,y-1),new Point(x+3,y-1),new Point(x,y+2)});
        }
    }
    sealed class MenuTabs:Control
    {
        public readonly string[] Tabs={"auto clicker","automation","experimental","misc"};public int Selected;public Action<int> Change;
        public MenuTabs(){Height=40;TabStop=true;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);}
        public void Select(int index){Selected=index;Invalidate();if(Change!=null)Change(index);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){Focus();Select(Math.Min(Tabs.Length-1,e.X*Tabs.Length/Width));}base.OnMouseDown(e);}
        protected override bool IsInputKey(Keys key){return key==Keys.Left||key==Keys.Right||base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){Select((Selected+(e.KeyCode==Keys.Right?1:Tabs.Length-1))%Tabs.Length);e.Handled=true;}base.OnKeyDown(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            for(int i=0;i<Tabs.Length;i++)
            {var r=new Rectangle(i*Width/Tabs.Length,0,Width/Tabs.Length,Height-5);Skin.TextAt(e.Graphics,Tabs[i],r,i==Selected?Color.White:Skin.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);using(var p=new Pen(i==Selected?Skin.Accent:Skin.Border,2))e.Graphics.DrawLine(p,r.Left+3,Height-5,r.Right-3,Height-5);}
        }
    }
}

