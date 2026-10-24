// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class ComponentSlot<T> where T : class, IDisposable
{
    private readonly Lock _lock = new();

    private bool _published;
    private PipelineComponent<T>? _current;
    private PipelineVersion _currentVersions;
    private PipelineComponent<T>? _pending;
    private PipelineVersion _pendingVersions;
    private readonly SeqLock<Snapshot> _snapshot = new(new());

    private class Snapshot
    {
        public bool Published { get; init; }
        public PipelineComponent<T>? Current { get; init; }
        public PipelineVersion CurrentVersions { get; init; }
        public PipelineComponent<T>? Pending { get; init; }
        public PipelineVersion PendingVersions { get; init; }
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

    public PipelineVersion CurrentVersions
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return snapshot.CurrentVersions;
        }
    }

    public PipelineVersion HighestVersions
    {
        get
        {
            Snapshot snapshot = _snapshot.Read();
            return PipelineVersion.Max(snapshot.CurrentVersions, snapshot.PendingVersions);
        }
    }

    public bool HasComponent => Latest is not null;

    public void Publish(PipelineComponent<T>? component, PipelineVersion versions)
    {
        lock (_lock)
        {
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