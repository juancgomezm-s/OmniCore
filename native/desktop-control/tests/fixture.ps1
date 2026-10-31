param([Parameter(Mandatory=$true)][string]$StatePath,[Parameter(Mandatory=$true)][string]$CommandPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Windows.Forms;
public class PrototypeFixture : Form {
    public TextBox Entry;
    public TextBox Secret;
    public Button Counter;
    public Panel DragArea;
    public Panel WheelArea;
    public Label Summary;
    public int Clicks=0, Wheels=0, EnterKeys=0, DragDistance=0;
    private Point dragStart;
    private bool dragging=false;
    public PrototypeFixture() {
        Text="CUA Prototype - Controlled Test";
        AccessibleName="CUA Controlled Test";
        Name="CuaControlledTest";
        ClientSize=new Size(660,480);
        StartPosition=FormStartPosition.Manual;
        Location=new Point(160,160);
        var heading=new Label {Text="Independent prototype test window",Location=new Point(20,12),Width=500};
        Controls.Add(heading);
        Entry=new TextBox {Name="TextInput",AccessibleName="Texto de prueba",Location=new Point(20,48),Width=390};
        Entry.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter) {EnterKeys++;e.SuppressKeyPress=true;}};
        Controls.Add(Entry);
        Secret=new TextBox {Name="PasswordInput",AccessibleName="Campo secreto",Location=new Point(20,88),Width=250,UseSystemPasswordChar=true,Text="Fixture-Secret-DoNotExpose"};
        Controls.Add(Secret);
        Counter=new Button {Name="CountButton",AccessibleName="Contar clic",Text="Contar clic",Location=new Point(440,45),Size=new Size(160,40)};
        Counter.Click+=(s,e)=>{Clicks++;RefreshSummary();};
        Controls.Add(Counter);
        DragArea=new Panel {Name="DragArea",AccessibleName="Zona de arrastre",Location=new Point(20,150),Size=new Size(300,110),BackColor=Color.LightSteelBlue,TabStop=true};
        DragArea.MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left) {dragStart=e.Location;dragging=true;}};
        DragArea.MouseUp+=(s,e)=>{if(dragging) {DragDistance=Math.Abs(e.X-dragStart.X)+Math.Abs(e.Y-dragStart.Y);dragging=false;RefreshSummary();}};
        Controls.Add(DragArea);
        WheelArea=new Panel {Name="WheelArea",AccessibleName="Zona de desplazamiento",Location=new Point(350,150),Size=new Size(250,180),AutoScroll=true,BackColor=Color.Beige,TabStop=true};
        WheelArea.Controls.Add(new Label {Text="Scroll inside this test area",Location=new Point(10,10),Size=new Size(180,900)});
        WheelArea.MouseWheel+=(s,e)=>{Wheels++;RefreshSummary();};
        Controls.Add(WheelArea);
        Summary=new Label {Name="ResultSummary",AccessibleName="Resultado",Location=new Point(20,360),Size=new Size(600,80)};
        Controls.Add(Summary);
        RefreshSummary();
    }
    public void RefreshSummary() {Summary.Text="Clicks="+Clicks+"; Wheel="+Wheels+"; Drag="+DragDistance;}
}
'@
[System.Windows.Forms.Application]::EnableVisualStyles()
$fixtureForm=New-Object PrototypeFixture
$fixtureTimer=New-Object System.Windows.Forms.Timer
$fixtureTimer.Interval=100
$fixtureTimer.Add_Tick({
    try {
        if(Test-Path -LiteralPath $CommandPath) {
            $fixtureCommand=Get-Content -LiteralPath $CommandPath -Raw | ConvertFrom-Json
            if($fixtureCommand.command -eq 'close') {$fixtureForm.Close();return}
            if($fixtureCommand.command -eq 'activate') {$fixtureForm.Activate()}
            if($fixtureCommand.command -eq 'move') {$fixtureForm.Location=New-Object System.Drawing.Point(260,200)}
            if($fixtureCommand.command -eq 'show_other' -and (-not $script:fixtureOther -or $script:fixtureOther.IsDisposed)) {
                $script:fixtureOther=New-Object System.Windows.Forms.Form
                $script:fixtureOther.Text='CUA Prototype - Focus Guard Test'
                $script:fixtureOther.Size=New-Object System.Drawing.Size(350,180)
                $script:fixtureOther.Show($fixtureForm)
                $script:fixtureOther.Activate()
            }
            if($fixtureCommand.command -eq 'hide_other' -and $script:fixtureOther) {$script:fixtureOther.Close()}
            if($fixtureCommand.command -eq 'freeze' -and -not $script:fixtureFrozeOnce) {
                $script:fixtureFrozeOnce=$true
                [System.IO.File]::WriteAllText(($CommandPath+'.ack'),'freeze')
                [System.Threading.Thread]::Sleep(12000)
            }
        }
        $fixtureControls=@{}
        foreach($fixturePair in @(@('entry',$fixtureForm.Entry),@('button',$fixtureForm.Counter),@('drag',$fixtureForm.DragArea),@('wheel',$fixtureForm.WheelArea))) {
            $fixtureRect=$fixturePair[1].RectangleToScreen($fixturePair[1].ClientRectangle)
            $fixtureControls[$fixturePair[0]]=@{hwnd=('0x{0:x}' -f $fixturePair[1].Handle.ToInt64());x=$fixtureRect.X;y=$fixtureRect.Y;width=$fixtureRect.Width;height=$fixtureRect.Height}
        }
        $fixtureState=@{pid=$PID;hwnd=('0x{0:x}' -f $fixtureForm.Handle.ToInt64());text=$fixtureForm.Entry.Text;clicks=$fixtureForm.Clicks;wheels=$fixtureForm.Wheels;scrollY=$fixtureForm.WheelArea.AutoScrollPosition.Y;drag_distance=$fixtureForm.DragDistance;enter_keys=$fixtureForm.EnterKeys;bounds=@{x=$fixtureForm.Left;y=$fixtureForm.Top;width=$fixtureForm.Width;height=$fixtureForm.Height};controls=$fixtureControls}
        [System.IO.File]::WriteAllText($StatePath,($fixtureState|ConvertTo-Json -Depth 5),[System.Text.UTF8Encoding]::new($false))
    } catch {}
})
$fixtureTimer.Start()
try {[void]$fixtureForm.ShowDialog()} finally {$fixtureTimer.Stop();$fixtureTimer.Dispose();$fixtureForm.Dispose()}
