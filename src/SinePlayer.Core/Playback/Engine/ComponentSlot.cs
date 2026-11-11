// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class ComponentSlot<T> where T : class
{
    private readonly Lock _lock = new();

    private bool _published;
    private PipelineComponent<T>? _current;
    private ChainVersion _currentVersions;
    private PipelineComponent<T>? _pending;
    private ChainVersion _pendingVersions;
    private readonly AtomicCell<Snapshot> _snapshot = new(new());

    private class Snapshot
    {
        public bool Published { get; init; }
        public PipelineComponent<T>? Current { get; init; }
        public ChainVersion CurrentVersions { get; init; }
        public PipelineComponent<T>? Pending { get; init; }
        public ChainVersion PendingVersions { get; init; }
    }

    public PipelineComponent<T>? Latest
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return snapshot.Published ? snapshot.Pending : snapshot.Current;
        }
    }

    public T? CurrentComponent
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return snapshot.Current?.Component;
        }
    }

    public MediaStreamInfo? CurrentStream
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return snapshot.Current?.Stream;
        }
    }

    public ChainVersion LastestVersions
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return ChainVersion.Max(snapshot.CurrentVersions, snapshot.PendingVersions);
        }
    }

    public ChainVersion CurrentVersions
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return snapshot.CurrentVersions;
        }
    }

    public bool HasComponent => Latest is not null;

    public void Publish(PipelineComponent<T>? component, ChainVersion versions)
    {
        lock (_lock)
        {
            if (ChainVersion.IsBehind(versions, ChainVersion.Max(_currentVersions, _pendingVersions)))
            {
                component?.Dispose();
                return;
            }

            PipelineComponent<T>? displaced = _published ? _pending : null;

            _published = true;
            _pending = component;
            _pendingVersions = versions;
            UpdateSnapshot();

            if (displaced is not null && !ReferenceEquals(displaced, component))
            {
                displaced.Dispose();
            }
        }
    }

    public bool Update(out T? oldComponent)
    {
        lock (_lock)
        {
            if (!_published)
            {
                oldComponent = null;
                return false;
            }

            oldComponent = _current?.Component;
            _current = _pending;
            _currentVersions = _pendingVersions;
            _published = false;
            _pending = null;
            _pendingVersions = default;
            UpdateSnapshot();
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            PipelineComponent<T>? current = _current;
            PipelineComponent<T>? published = _published ? _pending : null;

            _current = null;
            _currentVersions = default;
            _published = false;
            _pending = null;
            _pendingVersions = default;
            UpdateSnapshot();

            current?.Dispose();
            if (!ReferenceEquals(published, current))
            {
                published?.Dispose();
            }
        }
    }

    private void UpdateSnapshot()
    {
        _snapshot.Write(_ => new()
        {
            Published = _published,
            Current = _current,
            CurrentVersions = _currentVersions,
            Pending = _pending,
            PendingVersions = _pendingVersions,
        });
    }
}