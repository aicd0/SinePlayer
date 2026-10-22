// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Constants;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Views.Pages.Sidebar.MediaInfo;

namespace SinePlayer.Views.Pages.Sidebar.PlaybackInfo;

internal sealed partial class PlaybackInfoPage : BasePage
{
    private const int RefreshIntervalMilliseconds = 500;

    private readonly PlaybackInfoPageViewModel ViewModel = new();

    private readonly List<TextBlock?> _valueCells = [];
    private readonly List<string> _keys = [];

    private PlaybackInfoHandle? _playbackInfo;
    private DispatcherQueueTimer? _timer;

    public PlaybackInfoPage()
    {
        InitializeComponent();
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaChanged).ObserveSticky(this, args =>
        {
            _playbackInfo = args.PlaybackInfo;
            Refresh();
        });

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalMilliseconds);
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Refresh();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _timer?.Start();
        Refresh();
    }

    protected override void OnPause()
    {
        base.OnPause();
        _timer?.Stop();
    }

    protected override void OnStop()
    {
        base.OnStop();

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer = null;
        }

        _playbackInfo = null;
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        Refresh();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorResult copied = ClipboardUtils.SetText(ViewModel.BuildCopyText());
        copied.DisplayErrorMessage(PageActionHandler);
    }

    private void Refresh()
    {
        ViewModel.Update(_playbackInfo);

        IReadOnlyList<PlaybackInfoRow> rows = ViewModel.Rows;
        if (!HasSameStructure(rows))
        {
            BuildTable(rows);
            return;
        }

        WriteChangedValues(rows);
    }

    private bool HasSameStructure(IReadOnlyList<PlaybackInfoRow> rows)
    {
        if (rows.Count != _keys.Count)
        {
            return false;
        }

        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index].Key != _keys[index])
            {
                return false;
            }
        }

        return true;
    }

    private void BuildTable(IReadOnlyList<PlaybackInfoRow> rows)
    {
        TableGrid.Children.Clear();
        TableGrid.RowDefinitions.Clear();
        _valueCells.Clear();
        _keys.Clear();

        for (int index = 0; index < rows.Count; index++)
        {
            PlaybackInfoRow row = rows[index];
            _keys.Add(row.Key);
            TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (row.IsGroup)
            {
                TextBlock title = CreateCell("GroupTitleText", row.Name);
                Grid.SetRow(title, index);
                Grid.SetColumnSpan(title, 2);
                TableGrid.Children.Add(title);
                _valueCells.Add(null);
                continue;
            }

            TextBlock name = CreateCell("NameText", row.Name);
            Grid.SetRow(name, index);
            Grid.SetColumn(name, 0);
            TableGrid.Children.Add(name);

            TextBlock value = CreateCell("ValueText", row.Value);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            TableGrid.Children.Add(value);

            _valueCells.Add(value);
        }
    }

    private void WriteChangedValues(IReadOnlyList<PlaybackInfoRow> rows)
    {
        for (int index = 0; index < rows.Count && index < _valueCells.Count; index++)
        {
            if (_valueCells[index] is TextBlock cell && cell.Text != rows[index].Value)
            {
                cell.Text = rows[index].Value;
            }
        }
    }

    private TextBlock CreateCell(string styleKey, string text)
    {
        return new TextBlock
        {
            Style = (Style)Resources[styleKey],
            Text = text,
        };
    }
}
