using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Pathman.Gui.Controls;

internal static class EditingKeys
{
    public static bool InTextInput(KeyEventArgs e)=>EditableRow.FindParent<TextBoxBase>(e.OriginalSource as DependencyObject) is not null;
    public static bool Control=>Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
    public static bool SelectionModifier=>(Keyboard.Modifiers & (ModifierKeys.Control|ModifierKeys.Shift))!=0;
    public static bool Undo(KeyEventArgs e)=>Control && e.Key==Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
    public static bool Redo(KeyEventArgs e)=>Control && (e.Key==Key.Y || e.Key==Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    public static bool New(KeyEventArgs e)=>e.Key==Key.Insert && Keyboard.Modifiers==ModifierKeys.None || Control && e.Key==Key.N;
}
