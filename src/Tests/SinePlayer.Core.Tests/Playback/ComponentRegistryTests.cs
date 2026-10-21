// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Registry;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The registry is where a host says what to use, and it is read while it is being changed. These tests pin what
/// makes that possible: a component is its instance, the order is the order it was registered in, and a clone is
/// a copy rather than a window.
/// <para>
/// The components are the null backends, because they are what a test can register without a codec, a device or a
/// graphics card — and two of them are told apart by the instance, which is what the registry addresses them by.
/// </para>
/// </summary>
[TestFixture]
public class ComponentRegistryTests
{
    [Test]
    public void RefusesTheSameInstanceTwice()
    {
        ComponentRegistry registry = new();
        NullVideoDecoderFactory decoder = new();
        registry.Register(decoder);

        // Two entries of one instance would make "which one is running" unanswerable, and a removal could not say
        // which entry it meant, so the second is refused rather than appended after the first.
        InvalidOperationException? thrown = Assert.Throws<InvalidOperationException>(() => registry.Register(decoder));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.Message, Does.Contain(nameof(IVideoDecoderFactory)), "the message has to say which registration is wrong");
            Assert.That(thrown.Message, Does.Contain("Remove"), "the message has to say how to change what is there");
            Assert.That(Decoders(registry), Is.EqualTo(new[] { decoder }), "the refused registration must not have landed");
        }
    }

    [Test]
    public void RemovesAComponentAndLeavesTheRestInOrder()
    {
        ComponentRegistry registry = new();
        NullVideoDecoderFactory first = new();
        NullVideoDecoderFactory second = new("null:second");
        NullVideoDecoderFactory third = new("null:third");
        registry.Register(first);
        registry.Register(second);
        registry.Register(third);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.Remove(second), Is.True);
            Assert.That(Decoders(registry), Is.EqualTo(new[] { first, third }));
            Assert.That(registry.Remove(second), Is.False, "removing it twice is a host asking about something that is gone");
        }
    }

    [Test]
    public void ClearingARoleRemovesOnlyThatRole()
    {
        ComponentRegistry registry = new();
        NullVideoDecoderFactory decoder = new();
        NullVideoDecoderFactory otherDecoder = new("null:other");
        NullAudioRendererFactory audioRenderer = new();
        registry.Register(decoder);
        registry.Register(otherDecoder);
        registry.Register(audioRenderer);

        // "This role is now this one" must not require knowing what was there, which is the whole reason the
        // registry is the thing that is asked.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registry.Clear<IVideoDecoderFactory>(), Is.EqualTo(2));
            Assert.That(Decoders(registry), Is.Empty);
            Assert.That(registry.GetComponents<IAudioRendererFactory>(), Is.EqualTo(new[] { audioRenderer }), "another role is untouched");
            Assert.That(registry.Clear<IVideoDecoderFactory>(), Is.EqualTo(0), "clearing an empty role is nothing at all");
        }
    }

    [Test]
    public void ACloneDoesNotSeeLaterChanges()
    {
        ComponentRegistry registry = new();
        NullVideoDecoderFactory first = new();
        registry.Register(first);

        IComponentRegistry clone = registry.Clone();

        registry.Register(new NullVideoDecoderFactory("null:second"));
        registry.Remove(first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clone.GetComponents<IVideoDecoderFactory>(), Is.EqualTo(new[] { first }), "the clone kept what it copied");
            Assert.That(Decoders(registry).Count, Is.EqualTo(1), "the registry moved on");
            Assert.That(Decoders(registry)[0], Is.Not.SameAs(first));
        }
    }

    [Test]
    public void ListsAreCopiesRatherThanWindows()
    {
        ComponentRegistry registry = new();
        registry.Register(new NullVideoDecoderFactory());

        IReadOnlyList<IVideoDecoderFactory> list = Decoders(registry);
        registry.Register(new NullVideoDecoderFactory("null:second"));

        Assert.That(list.Count, Is.EqualTo(1), "a list a caller is holding must not grow under it");
    }

    /// <summary>
    /// A host changes registrations while the pipeline is asking what is registered. Both sides have to be safe,
    /// and every answer has to be one complete state rather than a mixture of two.
    /// </summary>
    [Test]
    public void StaysConsistentWhileRegistrationsAndReadsRace()
    {
        const int iterations = 2_000;
        ComponentRegistry registry = new();
        NullVideoDecoderFactory stable = new();
        registry.Register(stable);

        using CancellationTokenSource stop = new();
        Exception? failure = null;
        List<Thread> writers = [];
        for (int writer = 0; writer < 3; writer++)
        {
            int index = writer;
            Thread thread = new(() =>
            {
                try
                {
                    for (int step = 0; step < iterations; step++)
                    {
                        NullVideoDecoderFactory added = new($"w{index}-{step}");
                        registry.Register(added);
                        Assert.That(registry.Remove(added), Is.True);
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    stop.Cancel();
                }
            });
            thread.Start();
            writers.Add(thread);
        }

        int reads = 0;
        while (!stop.IsCancellationRequested)
        {
            IReadOnlyList<IVideoDecoderFactory> components = Decoders(registry);
            HashSet<IVideoDecoderFactory> seen = [];
            foreach (IVideoDecoderFactory component in components)
            {
                // A read that held one component twice would be two states stitched together, which is exactly
                // what the lock exists to prevent.
                Assert.That(seen.Add(component), Is.True, "a read returned a list with a duplicate component");
            }

            Assert.That(components.Count, Is.GreaterThanOrEqualTo(1), "the stable component is never removed");
            reads++;
        }

        foreach (Thread writer in writers)
        {
            writer.Join();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.Null, failure?.ToString());
            Assert.That(reads, Is.GreaterThan(0), "the reads have to have run while the writes did");
            Assert.That(Decoders(registry), Is.EqualTo(new[] { stable }), "every writer put back what it added");
        }
    }

    private static IReadOnlyList<IVideoDecoderFactory> Decoders(IComponentRegistry registry)
    {
        return registry.GetComponents<IVideoDecoderFactory>();
    }
}
