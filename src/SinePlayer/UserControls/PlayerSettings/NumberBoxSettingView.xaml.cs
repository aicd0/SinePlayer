// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;

namespace SinePlayer.UserControls.PlayerSettings;

internal sealed partial class NumberBoxSettingView : BaseUserControl
{
    public event EventHandler? ValueChanged;

    private bool _isSyncingValue;

    public NumberBoxSettingView()
    {
        InitializeComponent();

        Entry.ValueChanged += Entry_ValueChanged;

        // The box starts empty, so a value that is already the one it would show (zero) still has to be put in it.
        MainNumberBox.Value = Entry.Value;
    }

    //
    // Dependency Properties
    //

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(NumberBoxSettingView),
        new PropertyMetadata(string.Empty, OnLabelChanged));

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (NumberBoxSettingView)d;
        control.Entry.Label = (string)e.NewValue;
    }

    //
    // Editor
    //

    public double Value
    {
        get => Entry.Value;
        set => Entry.Value = value;
    }

    public double RestoreValue
    {
        get => Entry.RestoreValue;
        set => Entry.RestoreValue = value;
    }

    public double ResetValue
    {
        get => Entry.ResetValue;
        set => Entry.ResetValue = value;
    }

    public double Maximum
    {
        get => MainNumberBox.Maximum;
        set => MainNumberBox.Maximum = value;
    }

    public double Minimum
    {
        get => MainNumberBox.Minimum;
        set => MainNumberBox.Minimum = value;
    }

    public double LargeChange
    {
        get => MainNumberBox.LargeChange;
        set => MainNumberBox.LargeChange = value;
    }

    public double SmallChange
    {
        get => MainNumberBox.SmallChange;
        set => MainNumberBox.SmallChange = value;
    }

    public NumberBoxSpinButtonPlacementMode SpinButtonPlacementMode
    {
        get => MainNumberBox.SpinButtonPlacementMode;
        set => MainNumberBox.SpinButtonPlacementMode = value;
    }

    //
    // Events
    //

    private void MainNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isSyncingValue)
        {
            return;
        }

        _isSyncingValue = true;
        try
        {
            Entry.Value = MainNumberBox.Value;
        }
        finally
        {
            _isSyncingValue = false;
        }

        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Entry_ValueChanged(object? sender, EventArgs e)
    {
        if (_isSyncingValue)
        {
            return;
        }

        _isSyncingValue = true;
        try
        {
            if (MainNumberBox.Value != Entry.Value)
            {
                MainNumberBox.Value = Entry.Value;
            }
        }
        finally
        {
            _isSyncingValue = false;
        }

        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
