using System.Reflection;
using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class StorageLimitMathTests
    {
        private static readonly MethodInfo SetRefillEdgeMethod = typeof(PscStorageData).GetMethod(
            "SetRefillEdge",
            BindingFlags.Static | BindingFlags.NonPublic);

        [Test]
        public void ClampPair_ClampsBoundsAndNeverLeavesLowerAboveUpper()
        {
            int? lower = -50;
            int? upper = 500;

            PscDefLimit.ClampPair(ref lower, ref upper, max: 120);

            Assert.That(lower, Is.EqualTo(0));
            Assert.That(upper, Is.EqualTo(120));

            lower = 90;
            upper = 40;
            PscDefLimit.ClampPair(ref lower, ref upper, max: 120);

            Assert.That(lower, Is.EqualTo(40));
            Assert.That(upper, Is.EqualTo(40));
        }

        [Test]
        public void ClampPair_PreservesUnsetSides()
        {
            int? lower = null;
            int? upper = 500;

            PscDefLimit.ClampPair(ref lower, ref upper, max: 75);

            Assert.That(lower, Is.Null);
            Assert.That(upper, Is.EqualTo(75));

            lower = 9;
            upper = null;
            PscDefLimit.ClampPair(ref lower, ref upper, max: 75);

            Assert.That(lower, Is.EqualTo(9));
            Assert.That(upper, Is.Null);
        }

        [TestCase(null, 100, 0, true, PscRefillState.Refilling, ExpectedResult = PscRefillState.Unset)]
        [TestCase(10, 100, 100, true, PscRefillState.Refilling, ExpectedResult = PscRefillState.Satisfied)]
        [TestCase(10, 100, 10, false, PscRefillState.Satisfied, ExpectedResult = PscRefillState.Refilling)]
        [TestCase(10, 100, 11, false, PscRefillState.Satisfied, ExpectedResult = PscRefillState.Satisfied)]
        [TestCase(10, 100, 11, false, PscRefillState.Refilling, ExpectedResult = PscRefillState.Refilling)]
        [TestCase(10, 100, 11, false, PscRefillState.Unset, ExpectedResult = PscRefillState.Refilling)]
        public PscRefillState RefillEdge_MatchesHysteresisTruthTable(
            int? lower,
            int? upper,
            int count,
            bool full,
            PscRefillState previous)
        {
            return NextRefillState(lower, upper, count, full, previous);
        }

        [Test]
        public void RefillEdge_DoesNotChatterInsideDeadband()
        {
            const int lower = 10;
            const int upper = 30;
            int[] walk = { 0, 6, 10, 11, 12, 18, 29, 30, 28, 20, 11, 10, 14, 29, 30 };
            var state = PscRefillState.Unset;

            foreach (int count in walk)
            {
                var before = state;
                bool full = count >= upper;
                state = NextRefillState(lower, upper, count, full, state);

                if (full)
                {
                    Assert.That(state, Is.EqualTo(PscRefillState.Satisfied), "Full rail must turn refill off.");
                }
                else if (count <= lower)
                {
                    Assert.That(state, Is.EqualTo(PscRefillState.Refilling), "Lower rail must turn refill on.");
                }
                else if (before != PscRefillState.Unset)
                {
                    Assert.That(state, Is.EqualTo(before), "Deadband must preserve the prior edge state.");
                }
            }
        }

        private static PscRefillState NextRefillState(
            int? lower,
            int? upper,
            int count,
            bool full,
            PscRefillState previous)
        {
            var limit = new PscDefLimit { Lower = lower, Upper = upper, refill = previous };
            SetRefillEdgeMethod.Invoke(null, new object[] { limit, count, full });
            return limit.refill;
        }
    }
}
