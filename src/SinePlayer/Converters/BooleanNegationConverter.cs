// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using Microsoft.UI.Xaml.Data;

namespace SinePlayer.Converters;

/// <summary>
/// Value converter that translates true to false and vice versa.
/// </summary>
internal partial class BooleanNegationConverter : IValueConverter
{
    public object Convert(object value, Type target_type, object parameter, string language)
    {
        bool? boxed_bool = value as bool?;
        bool bool_value = (boxed_bool != null && boxed_bool.Value);
        return !bool_value;
    }

    public object ConvertBack(object value, Type target_type, object parameter, string language)
    {
        bool? boxed_bool = (value as bool?);
        bool bool_value = (boxed_bool != null && boxed_bool.Value);
        return !bool_value;
    }
}