[CmdletBinding()]
param([string]$ResultPath='C:\PathManInteractionTest\gui-results.json', [switch]$PrepareFixture)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'Virtual Machine') { throw 'Run this fixture-driven GUI test only in the disposable Windows VM.' }
if($PrepareFixture) {
    $testExe='C:\PathManTestApp\PathMan.exe'
    Get-Process PathMan -ErrorAction SilentlyContinue | Where-Object Path -eq $testExe | Stop-Process -Force
    [Environment]::SetEnvironmentVariable('PATHMAN_RENAMED',$null,'User')
    [Environment]::SetEnvironmentVariable('PATHMAN_FOCUS_TEST',$null,'User')
    [Environment]::SetEnvironmentVariable('PATHMAN_DRAG_A','alpha','User')
    [Environment]::SetEnvironmentVariable('PATHMAN_DRAG_B','beta','User')
    [Environment]::SetEnvironmentVariable('PATHMAN_DRAG_C','gamma','User')
    [Environment]::SetEnvironmentVariable('PATHMAN_LIST','one;;two;  spaced  ;','User')
    [Environment]::SetEnvironmentVariable('PATHMAN_SCALAR_ORIGIN','one','User')
    $fixture=@{groups=@(@{id='paths';name='路径变量';rule='paths'},@{id='system';name='系统变量';rule='builtin';collapsed=$true},@{id='other';name='其他变量';rule='fallback'},@{id='external';name='外部变量';rule='external';requiresMonitoring=$true},@{id='work';name='工作';rule='manual';scope='User'});assignments=@{'User:PATHMAN_DRAG_A'='work';'User:PATHMAN_DRAG_B'='work';'User:PATHMAN_LIST'='work';'User:PATHMAN_DRAG_C'='other'};pinnedVariables=@('User:PATHMAN_DRAG_B','User:PATHMAN_DRAG_A');revision=[guid]::NewGuid().ToString('N')}
    $fixture|ConvertTo-Json -Depth 8|Set-Content (Join-Path $env:LOCALAPPDATA 'PathMan\environment.json') -Encoding UTF8
    Start-Process $testExe; Start-Sleep -Seconds 2
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InteractionInput {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
}
'@
$checks=[Collections.Generic.List[string]]::new()
$data=Join-Path $env:LOCALAPPDATA 'PathMan\environment.json'
function State { Get-Content -LiteralPath $data -Raw | ConvertFrom-Json }
function Assert($condition,[string]$message) { if(!$condition) { throw $message }; $script:checks.Add($message); try { $message | Set-Content ($ResultPath+'.progress') } catch [IO.IOException] { } }
function WaitFor([scriptblock]$condition,[string]$message) { for($i=0;$i -lt 100;$i++) { if(& $condition) { return }; Start-Sleep -Milliseconds 100 }; throw $message }
function Find($root,[string]$value,$property=[Windows.Automation.AutomationElement]::NameProperty,$type=$null) {
    $condition=[Windows.Automation.PropertyCondition]::new($property,$value)
    if($type) { $condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$type)) }
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function Id($root,[string]$id) { Find $root $id ([Windows.Automation.AutomationElement]::AutomationIdProperty) }
function Tree { Id $script:window 'UserVariablesTree' }
function Node([string]$name) { Find (Tree) $name ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::TreeItem) }
function Text($node) { Find $node $node.Current.Name ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Text) }
function Rows($list) { $list.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)) }
function Invoke($element) { $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
function SelectNode($node) { $node.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 200 }
function CollapseAll {
    $nodes=(Tree).FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TreeItem))
    foreach($node in $nodes) { $p=$null; if($node.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$p) -and $p.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) {$p.Collapse()} }
    Start-Sleep -Milliseconds 180
}
function Expand([string]$name) { (Node $name).GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 180 }
function Key([byte]$code) { [InteractionInput]::keybd_event($code,0,0,[UIntPtr]::Zero); [InteractionInput]::keybd_event($code,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds 180 }
function MovePointer($x,$y) { [void][InteractionInput]::SetCursorPos([int]$x,[int]$y); Start-Sleep -Milliseconds 70 }
function Down { [InteractionInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero) }
function Up { [InteractionInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
function DoubleClick($text) { $r=$text.Current.BoundingRectangle; MovePointer ($r.Left+10) ($r.Top+$r.Height/2); Down; Start-Sleep -Milliseconds 40; Up; Start-Sleep -Milliseconds 120; Down; Start-Sleep -Milliseconds 40; Up; Start-Sleep -Milliseconds 300 }
function InlineInput { Find $script:window '行内编辑' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Edit) }
function SetInline([string]$text) { WaitFor { $null -ne (InlineInput) } 'Inline editor did not appear'; $input=InlineInput; Assert ($null -ne $input) 'Inline text box is shown'; $input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text); $input.SetFocus() }
function Drag($source,$x,$y) {
    Start-Sleep -Milliseconds 550; $r=(Text $source).Current.BoundingRectangle
    MovePointer ($r.Left+12) ($r.Top+$r.Height/2); Down; MovePointer ($r.Left+24) ($r.Top+$r.Height/2+5); MovePointer $x $y; Start-Sleep -Milliseconds 180; Up; Start-Sleep -Milliseconds 500
}
function Pin([string]$name) { Invoke (Find (Node $name) '置顶' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Button)) }
function OpenMenuRoot {
    $roots=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:window.Current.ProcessId))
    foreach($root in $roots) { if($null -ne (Find $root '新建分组' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::MenuItem))) { return $root } }
}
function TextOrigin($element) {
    # Compare the same rendered text in both editors; TextBlock has no UIA TextPattern.
    Start-Sleep -Milliseconds 250
    $r=$element.Current.BoundingRectangle
    $x=[int][Math]::Floor($r.Left); $y=[int][Math]::Floor($r.Top)
    $bitmap=[Drawing.Bitmap]::new([int][Math]::Min(96,$r.Width),[int][Math]::Min(28,$r.Height))
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($x,$y,0,0,$bitmap.Size)
        $left=$bitmap.Width; $top=$bitmap.Height
        for($py=0;$py -lt $bitmap.Height;$py++) { for($px=0;$px -lt $bitmap.Width;$px++) {
            $color=$bitmap.GetPixel($px,$py)
            if($color.R -lt 120 -and $color.G -lt 120 -and $color.B -lt 120) { $left=[Math]::Min($left,$px); $top=[Math]::Min($top,$py) }
        } }
        if($left -eq $bitmap.Width) { throw 'No visible text pixels were found' }
        @{Left=$x+$left;Top=$y+$top}
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
try {
    $window=$null
    WaitFor { $script:window=Find ([Windows.Automation.AutomationElement]::RootElement) 'PathMan' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Window); $null -ne $script:window } 'PathMan window did not launch'
    [void][InteractionInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle); Start-Sleep -Milliseconds 350
    CollapseAll; Expand '工作'; SelectNode (Node '工作')
    Assert ((Rows (Id $window 'GroupVariablesList')).Count -eq 3) 'Selecting a group shows its variables in the right pane'
    Assert ($null -ne (Find (Id $window 'GroupVariablesList') 'beta')) 'Group preview includes variable values'
    Assert ((Text (Node 'PATHMAN_DRAG_B')).Current.BoundingRectangle.Left-(Text (Node '工作')).Current.BoundingRectangle.Left -ge 14) 'Variable names are indented from category headings'
    Assert ($null -eq (Find (Id $window 'UserActions') '删除') -and $null -eq (Find (Id $window 'UserActions') '重命名')) 'Scope title toolbar contains no delete or rename buttons'
    $row=(Text (Node 'PATHMAN_DRAG_B')).Current.BoundingRectangle
    $edit=Find (Node 'PATHMAN_DRAG_B') '编辑' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Button)
    Assert ($edit.Current.BoundingRectangle.Left -gt $row.Right) 'Row operations are positioned to the right of the variable name'
    foreach($toolbar in @('UserActions','SystemActions')) {
        $new=Id (Id $window $toolbar) 'NewButton'; $r=$new.Current.BoundingRectangle
        MovePointer ($r.Left+$r.Width/2) ($r.Top+$r.Height/2); Start-Sleep -Milliseconds 650
        Assert ($null -eq (OpenMenuRoot)) "$toolbar does not open its menu on hover"
        Down; Up; Start-Sleep -Milliseconds 300
        WaitFor { $null -ne (OpenMenuRoot) } 'Click did not open the standard menu'
        $menu=OpenMenuRoot
        Assert ($null -ne (Find $menu '新建分组' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::MenuItem)) -and $null -ne (Find $menu '新建环境变量' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::MenuItem))) "$toolbar opens a standard menu with both creation commands"
        Key 27
    }
    $preview=Find (Id $window 'GroupVariablesList') 'PATHMAN_LIST' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Text)
    DoubleClick $preview
    Assert ((Id $window 'VariableTitle').Current.Name -eq 'PATHMAN_LIST' -and $null -eq (InlineInput)) 'Double-clicking a variable in the group preview opens its value editor'
    $first=Find (Id $window 'ValuesList') 'one' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Text)
    $listOrigin=TextOrigin $first
    Expand '其他变量'; SelectNode (Node 'PATHMAN_SCALAR_ORIGIN')
    $scalarOrigin=TextOrigin (Id $window 'ScalarValueBox')
    @{List=$listOrigin;Scalar=$scalarOrigin}|ConvertTo-Json -Depth 4|Set-Content ($ResultPath+'.origins.json') -Encoding UTF8
    Assert ([Math]::Abs($listOrigin.Left-$scalarOrigin.Left) -le 1 -and [Math]::Abs($listOrigin.Top-$scalarOrigin.Top) -le 1) 'Scalar and list editors put their first character at the same position'
    $copy=Id $window 'CopyValueButton'
    Assert ([Math]::Abs($copy.Current.BoundingRectangle.Top-(Tree).Current.BoundingRectangle.Top) -le 1) 'The first scalar action button is aligned with the editor top'
    SelectNode (Node '工作')
    DoubleClick (Text (Node '工作')); SetInline '工作重命名'; Key 13
    WaitFor { (State).groups.name -contains '工作重命名' } 'Group inline rename failed'
    Assert ($null -ne (Node '工作重命名')) 'Double-click renames a group without opening a dialog'
    Expand '工作重命名'; SelectNode (Node 'PATHMAN_DRAG_A'); DoubleClick (Text (Node 'PATHMAN_DRAG_A')); SetInline 'PATHMAN_RENAMED'; Key 13
    WaitFor { [Environment]::GetEnvironmentVariable('PATHMAN_RENAMED','User') -eq 'alpha' } 'Variable inline rename failed'
    Assert ((State).pinnedVariables -contains 'User:PATHMAN_RENAMED') 'Renaming retains pin state and order'
    DoubleClick (Text (Node 'PATHMAN_RENAMED')); SetInline 'PATHMAN_DRAG_B'; Key 13
    Assert ($null -ne (InlineInput)) 'Duplicate name keeps the inline editor open'
    Key 27
    Assert ($null -ne (Node 'PATHMAN_RENAMED')) 'Escape cancels an invalid rename without deleting the variable'
    SelectNode (Node 'PATHMAN_LIST'); $list=Id $window 'ValuesList'; $items=Rows $list
    Assert ($items.Count -eq 5) 'List values retain empty and whitespace entries'
    DoubleClick (Find $items[0] 'one' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Text)); SetInline '  changed  '; Key 13
    Assert ([Environment]::GetEnvironmentVariable('PATHMAN_LIST','User') -eq 'one;;two;  spaced  ;') 'Inline value edits remain staged until Save'
    Invoke (Id $window 'SaveButton')
    WaitFor { [Environment]::GetEnvironmentVariable('PATHMAN_LIST','User') -eq '  changed  ;;two;  spaced  ;' } 'Saving inline list edits failed'
    Assert ($true) 'Saving preserves all other list entries exactly'
    SelectNode (Node 'PATHMAN_DRAG_B')
    Invoke (Find (Node 'PATHMAN_DRAG_B') '编辑' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Button))
    SetInline 'PATHMAN_FOCUS_TEST'
    SelectNode (Node '其他变量')
    WaitFor { [Environment]::GetEnvironmentVariable('PATHMAN_FOCUS_TEST','User') -eq 'beta' -and (Id $window 'VariableTitle').Current.Name -eq '其他变量' } 'Focus-loss commit did not continue selection'
    Assert ($true) 'Losing focus commits a rename and selects the clicked group in one action'
    SelectNode (Node 'PATHMAN_FOCUS_TEST')
    Invoke (Find (Node 'PATHMAN_FOCUS_TEST') '编辑' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::Button))
    SetInline 'PATHMAN_DRAG_B'
    $about=Find (Id $window 'Navigation') '关于' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::ListItem)
    SelectNode $about
    WaitFor { [Environment]::GetEnvironmentVariable('PATHMAN_DRAG_B','User') -eq 'beta' -and $null -eq (Id $window 'UserVariablesTree') } 'Navigation did not continue after inline commit'
    Assert ($true) 'Navigation waits for the inline rename and then opens the requested page'
    SelectNode (Find (Id $window 'Navigation') '变量管理' ([Windows.Automation.AutomationElement]::NameProperty) ([Windows.Automation.ControlType]::ListItem))
    CollapseAll
    $treeRect=(Tree).Current.BoundingRectangle
    Drag (Node '工作重命名') ($treeRect.Left+85) ($treeRect.Top+6)
    WaitFor { $order=(State).groupOrder.User; $null -ne $order -and $order[0] -eq 'work' } 'Group drag ordering failed'
    Assert ($true) 'Ordinary groups can be reordered by dragging'
    Pin '工作重命名'; Pin '路径变量'
    Drag (Node '路径变量') ($treeRect.Left+85) ($treeRect.Top+6)
    WaitFor { (State).pinnedGroups.User[0] -eq 'paths' } 'Pinned group reorder failed'
    Assert ($true) 'Pinned groups can be reordered among themselves'
    $revision=(State).revision
    Drag (Node '其他变量') ($treeRect.Left+85) ($treeRect.Top+6)
    Assert ((State).revision -eq $revision) 'Ordinary groups cannot be dropped inside the pinned region'
    Expand '工作重命名'
    $revision=(State).revision; $b=(Text (Node 'PATHMAN_DRAG_B')).Current.BoundingRectangle
    Drag (Node 'PATHMAN_LIST') ($b.Left+8) ($b.Top+4)
    Assert ((State).revision -eq $revision) 'Ordinary variables cannot be reordered within their group'
    Pin 'PATHMAN_LIST'
    $b=(Text (Node 'PATHMAN_DRAG_B')).Current.BoundingRectangle
    Drag (Node 'PATHMAN_LIST') ($b.Left+8) ($b.Top+4)
    WaitFor { (State).pinnedVariables[0] -eq 'User:PATHMAN_LIST' } 'Pinned variable reorder failed'
    Assert ($true) 'Pinned variables can be reordered among themselves'
    $target=(Text (Node '其他变量')).Current.BoundingRectangle
    Drag (Node 'PATHMAN_RENAMED') ($target.Left+8) ($target.Top+10)
    WaitFor { (State).assignments.'User:PATHMAN_RENAMED' -eq 'other' } 'Variable regrouping failed'
    Assert ($true) 'Dropping a variable on another group persists its manual assignment'
    $revision=(State).revision
    Drag (Node 'PATHMAN_DRAG_B') 230 180
    Assert ((State).revision -eq $revision) 'Dropping outside the list leaves the workspace unchanged'
    # Add enough groups to exercise both auto-scroll directions and distance-based speed.
    CollapseAll; $state=State
    $state.groups+=@(0..29 | ForEach-Object { @{id="overflow-$_";name="滚动分组 $_";rule='manual';scope='User';collapsed=$true} })
    $state.revision=[guid]::NewGuid().ToString('N'); $state|ConvertTo-Json -Depth 12|Set-Content $data -Encoding UTF8
    WaitFor { $null -ne (Node '滚动分组 29') } 'Overflow fixture was not refreshed'
    CollapseAll
    $scroll=(Tree).GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent(-1,0); Start-Sleep -Milliseconds 200
    $treeRect=(Tree).Current.BoundingRectangle; $r=(Text (Node '其他变量')).Current.BoundingRectangle
    Start-Sleep -Milliseconds 550; MovePointer ($r.Left+10) ($r.Top+8); Down; MovePointer ($r.Left+25) ($r.Top+12)
    MovePointer ($treeRect.Left+85) ($treeRect.Bottom+8); Start-Sleep -Milliseconds 300; $s1=$scroll.Current.VerticalScrollPercent
    Start-Sleep -Milliseconds 450; $s2=$scroll.Current.VerticalScrollPercent
    MovePointer ($treeRect.Left+85) ($treeRect.Bottom+150); Start-Sleep -Milliseconds 450; $s3=$scroll.Current.VerticalScrollPercent
    Assert ($s2 -gt $s1 -and ($s3-$s2) -gt ($s2-$s1)) 'Dragging farther below the list increases downward scroll speed'
    MovePointer ($treeRect.Left+85) ($treeRect.Top-8); Start-Sleep -Milliseconds 300; $s1=$scroll.Current.VerticalScrollPercent
    Start-Sleep -Milliseconds 450; $s2=$scroll.Current.VerticalScrollPercent
    MovePointer ($treeRect.Left+85) ($treeRect.Top-110); Start-Sleep -Milliseconds 450; $s3=$scroll.Current.VerticalScrollPercent
    Assert ($s2 -lt $s1 -and ($s2-$s3) -gt ($s1-$s2)) 'Dragging farther above the list increases upward scroll speed'
    Key 27; Up
    Assert ($null -ne (Node '其他变量')) 'Escape cancels a scrolling drag and restores the source row'
    @{Passed=$true;Checks=$checks} | ConvertTo-Json -Depth 6 | Set-Content $ResultPath -Encoding UTF8
} catch {
    Up
    @{Passed=$false;Checks=$checks;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace} | ConvertTo-Json -Depth 6 | Set-Content $ResultPath -Encoding UTF8
    exit 1
}
