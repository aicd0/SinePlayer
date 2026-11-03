// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

using SinePlayer.Common.BaseUI;

namespace SinePlayer.UserControls.PlayerSettings;

public sealed partial class PlayerSettingsSection : BaseUserControl
{
    private const double IndentStep = 12.0;
    private const int MaximumIndentLevels = 2;

    private const string ExpandedGlyph = "\uE70D";
    private const string CollapsedGlyph = "\uE76C";

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PlayerSettingsSection), new PropertyMetadata(string.Empty, (sender, _) => ((PlayerSettingsSection)sender).UpdateTitle()));

    public static readonly DependencyProperty SectionKeyProperty = DependencyProperty.Register(
        nameof(SectionKey), typeof(string), typeof(PlayerSettingsSection), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(PlayerSettingsSection), new PropertyMetadata(true, (sender, args) => ((PlayerSettingsSection)sender).OnIsExpandedChanged((bool)args.NewValue)));

    private UIElement? _sectionContent;

    public PlayerSettingsSection()
    {
        InitializeComponent();
        Loaded += OnSectionLoaded;
        UpdateExpandedVisuals(IsExpanded);
    }

    public event Action<PlayerSettingsSection, bool>? ExpandedChanged;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string SectionKey
    {
        get => (string)GetValue(SectionKeyProperty);
        set => SetValue(SectionKeyProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public UIElement? SectionContent
    {
        get => _sectionContent;
        set
        {
            _sectionContent = value;
            SectionContentPanel.Children.Clear();
            if (value is not null)
            {
                SectionContentPanel.Children.Add(value);
            }
        }
    }

    internal IEnumerable<object> SectionChildren => SectionContentPanel.Children;

    internal int NestingDepth
    {
        get
        {
            int depth = 0;
            DependencyObject? current = this;
            while (current is not null)
            {
                current = VisualTreeHelper.GetParent(current);
                if (current is PlayerSettingsSection)
                {
                    depth++;
                }
            }

            return depth;
        }
    }

    internal void ApplyNestingDepth()
    {
        int level = Math.Min(NestingDepth, MaximumIndentLevels);
        HeaderRow.Padding = new Thickness(level * IndentStep, 0, 0, 0);
    }

    private void UpdateTitle()
    {
        TitleText.Text = Title;
        AutomationProperties.SetName(HeaderRow, Title);
    }

    private void OnIsExpandedChanged(bool isExpanded)
    {
        UpdateExpandedVisuals(isExpanded);
        ExpandedChanged?.Invoke(this, isExpanded);
    }

    private void UpdateExpandedVisuals(bool isExpanded)
    {
        ChevronIcon.Glyph = isExpanded ? ExpandedGlyph : CollapsedGlyph;
        SectionContentPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSectionLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNestingDepth();
    }

    private void HeaderRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        IsExpanded = !IsExpanded;
    }
}
