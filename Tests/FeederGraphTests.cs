using System.Collections.Generic;
using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class FeederGraphTests
    {
        [Test]
        public void AddEdgeRejectsInvalidSelfAndDuplicateEdges()
        {
            var links = new PscFeederLinks();

            Assert.That(links.AddEdge(null, "B"), Is.False);
            Assert.That(links.AddEdge("A", ""), Is.False);
            Assert.That(links.AddEdge("A", "A"), Is.False);

            Assert.That(links.AddEdge("A", "B"), Is.True);
            Assert.That(links.AddEdge("A", "B"), Is.False);
            Assert.That(links.HasEdge("A", "B"), Is.True);
            Assert.That(links.HasAnyDestination("A"), Is.True);
            Assert.That(links.HasAnySource("B"), Is.True);
            Assert.That(links.SourceCount("B"), Is.EqualTo(1));
            Assert.That(links.DestinationCount("A"), Is.EqualTo(1));
        }

        [Test]
        public void ReachabilityHandlesMultiHopCyclesWithoutReturningSeed()
        {
            var links = new PscFeederLinks();
            links.AddEdge("A", "B");
            links.AddEdge("B", "C");
            links.AddEdge("C", "A");

            Assert.That(links.IsDownstreamReachable("A", "C"), Is.True);
            Assert.That(links.IsDownstreamReachable("A", "A"), Is.False);

            var upstream = links.UpstreamReachableFrom("C");
            Assert.That(upstream, Does.Contain("A"));
            Assert.That(upstream, Does.Contain("B"));
            Assert.That(upstream, Does.Not.Contain("C"));
        }

        [Test]
        public void ComputeChainDistancesBfsesBothDirectionsFromSeeds()
        {
            var links = new PscFeederLinks();
            links.AddEdge("A", "B");
            links.AddEdge("B", "C");
            links.AddEdge("C", "D");

            var down = new Dictionary<string, int>();
            var up = new Dictionary<string, int>();
            var queue = new Queue<string>();

            links.ComputeChainDistances(new HashSet<string> { "B" }, down, up, queue);

            Assert.That(down, Does.ContainKey("B").WithValue(0));
            Assert.That(down, Does.ContainKey("C").WithValue(1));
            Assert.That(down, Does.ContainKey("D").WithValue(2));
            Assert.That(up, Does.ContainKey("B").WithValue(0));
            Assert.That(up, Does.ContainKey("A").WithValue(1));
        }

        [Test]
        public void AdoptLinksMirrorsInboundAndOutboundEdges()
        {
            var links = new PscFeederLinks();
            links.AddEdge("A", "B");
            links.AddEdge("B", "C");

            links.AdoptLinks("B", "X");

            Assert.That(links.HasEdge("A", "X"), Is.True);
            Assert.That(links.HasEdge("X", "C"), Is.True);
            Assert.That(links.HasEdge("X", "X"), Is.False);
        }

        [Test]
        public void PruneToLiveIdsDropsAnyEdgeWithDeadEndpoint()
        {
            var links = new PscFeederLinks();
            links.AddEdge("A", "B");
            links.AddEdge("B", "C");

            Assert.That(links.PruneToLiveIds(new HashSet<string> { "A", "B" }), Is.True);

            Assert.That(links.HasEdge("A", "B"), Is.True);
            Assert.That(links.HasEdge("B", "C"), Is.False);
        }
    }
}
