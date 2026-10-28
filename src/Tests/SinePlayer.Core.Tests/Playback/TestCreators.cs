// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Creators for the stand-in factories in these tests.
/// <para>
/// A factory offers an <see cref="IComponentCreator{TComponent}"/> before anything is built, which is how the
/// pipeline decides whether a component is a change at all. A stand-in that produces one thing therefore states
/// what it would produce and hands it over when asked, and a stand-in that wraps a real backend wraps the
/// creator rather than the decoder — so the real thing is still only built when the pipeline wants it.
/// </para>
/// </summary>
internal static class TestCreators
{
    /// <summary>
    /// Turns an offer from a backend into an offer of that backend's component, decorated. The signature is the
    /// inner one's, because a wrapper is the same decoder as the one it wraps: it changes the timing or the
    /// contents, not what is running.
    /// </summary>
    internal static OperationResult<IComponentCreator<TComponent>> Wrap<TComponent>(OperationResult<IComponentCreator<TComponent>> offered, Func<TComponent, TComponent> decorate)
        where TComponent : class
    {
        if (!offered.IsSuccessful)
        {
            return offered;
        }

        return new OperationResultCreator<IComponentCreator<TComponent>>().Success(new DecoratingCreator<TComponent>(offered.Result, decorate));
    }

    private sealed class DecoratingCreator<TComponent>(IComponentCreator<TComponent> inner, Func<TComponent, TComponent> decorate) : IComponentCreator<TComponent>
        where TComponent : class
    {
        public string Signature => inner.Signature;

        public OperationResult Create(out TComponent? component)
        {
            OperationResult created = inner.Create(out TComponent? made);
            component = created.IsSuccessful && made is not null ? decorate(made) : null;
            return created;
        }
    }
}

/// <summary>
/// A creator that states one component and hands it over: what a stand-in factory that makes its own component
/// offers, as opposed to one wrapping a real backend.
/// </summary>
internal sealed class FixedCreator<TComponent>(string signature, Func<TComponent?> make) : IComponentCreator<TComponent>
    where TComponent : class
{
    public string Signature => signature;

    public OperationResult Create(out TComponent? component)
    {
        component = make();
        return component is null
            ? OperationResult.Failure($"{Signature} could not produce a component.")
            : OperationResult.Success();
    }
}
