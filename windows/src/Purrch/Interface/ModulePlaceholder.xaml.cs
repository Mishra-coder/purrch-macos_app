using System.Windows.Controls;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

public partial class ModulePlaceholder : UserControl
{
    public ModulePlaceholder(ModuleId module, bool glow)
    {
        InitializeComponent();
        Glyph.Text = module.Glyph();
        // Glowing themes fill the badge with the accent, so the glyph takes the background colour.
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, glow ? ThemeResources.Background : ThemeResources.Accent);
        Title.Text = module.Title();
        Subtitle.Text = module.Subtitle();
    }
}
