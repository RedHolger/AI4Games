using System;
using NUnit.Framework;
using Orchard;

namespace Orchard.Tests
{
    // Tests the real utility AI in Assets/Orchard/Scripts/ShamblerUtilityAI.cs.
    // Decisions are forced by calling update(0.3), which is longer than the 0.25 s think
    // interval. No scoring formula is reimplemented in this file.
    [TestFixture]
    public class ShamblerUtilityTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld(90210);
            world.Fill(C.GRASS);
            // Keep the two dogs out of smelling range unless a test moves them.
            world.game.pip.x = 40;
            world.game.pip.y = 40;
            world.game.thistle.x = 60;
            world.game.thistle.y = 40;
            world.game.thistle.state = ThistleState.Flee;
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
        }

        static void ForceThink(Shambler s)
        {
            s.update(0.3);
        }

        // ---------------- utility maths ----------------

        [Test]
        public void Utility_Near_IsClampedToOneAtZeroAndToZeroAtRange()
        {
            Assert.AreEqual(1.0, Utility.Near(0, 100), 1e-12);
            Assert.AreEqual(0.5, Utility.Near(50, 100), 1e-12);
            Assert.AreEqual(0.0, Utility.Near(100, 100), 1e-12);
            Assert.AreEqual(0.0, Utility.Near(150, 100), 1e-12);
            Assert.AreEqual(1.0, Utility.Near(-50, 100), 1e-12);
        }

        [Test]
        public void Utility_Clamp01_ClampsBothEnds()
        {
            Assert.AreEqual(0.0, Utility.Clamp01(-3));
            Assert.AreEqual(0.35, Utility.Clamp01(0.35), 1e-12);
            Assert.AreEqual(1.0, Utility.Clamp01(3));
        }

        [Test]
        public void Utility_Score_WithOneConsideration_IsWeightTimesValue()
        {
            Assert.AreEqual(0.62, Utility.Score(0.62, 1.0), 1e-12);
            Assert.AreEqual(0.31, Utility.Score(0.62, 0.5), 1e-12);
            Assert.AreEqual(0.0, Utility.Score(0.62, 0.0), 1e-12);
        }

        [Test]
        public void Utility_Score_CompensatesForManyConsiderations()
        {
            double one = Utility.Score(1.0, 0.5);
            double two = Utility.Score(1.0, 0.5, 0.5);
            double three = Utility.Score(1.0, 0.5, 0.5, 0.5);

            Assert.AreEqual(0.5, one, 1e-12);
            Assert.AreEqual(0.34375, two, 1e-12);
            Assert.Less(two, one);
            Assert.Less(three, two);
            Assert.Greater(two, 0.5 * 0.5, "compensation must lift the raw product");
        }

        [Test]
        public void Utility_Score_ScalesWithWeight()
        {
            Assert.AreEqual(0.85, Utility.Score(0.85, 1.0, 1.0), 1e-12);
        }

        // ---------------- health, damage and scores ----------------

        [Test]
        public void Shambler_Health_StartsFullAndDiesAtZero()
        {
            Shambler s = world.AddShambler(300, 300);

            Assert.AreEqual(Shambler.MAX_HP, s.hp, 1e-12);
            Assert.AreEqual(1.0, s.HpFrac, 1e-12);
            Assert.IsFalse(s.dead);

            s.hit(2.5);
            Assert.AreEqual(3.5, s.hp, 1e-12);
            Assert.AreEqual(3.5 / Shambler.MAX_HP, s.HpFrac, 1e-12);

            s.hit(100);
            Assert.LessOrEqual(s.hp, 0);
            Assert.IsTrue(s.dead);
        }

        [Test]
        public void Think_ProducesFiniteBoundedScoresForEveryAction()
        {
            world.AddTree(4, 3);
            world.AddBait(340, 300);
            world.game.night = 2;
            Shambler s = world.AddShambler(320, 300);
            world.AddBird(330, 290, false);

            ForceThink(s);

            Assert.AreEqual(System.Enum.GetValues(typeof(ShamAct)).Length, s.scores.Length);
            for (int i = 0; i < s.scores.Length; i++)
            {
                Assert.IsFalse(double.IsNaN(s.scores[i]), "score " + (ShamAct)i + " is NaN");
                Assert.IsFalse(double.IsInfinity(s.scores[i]), "score " + (ShamAct)i + " is infinite");
                Assert.GreaterOrEqual(s.scores[i], 0.0, "score " + (ShamAct)i + " is negative");
                Assert.LessOrEqual(s.scores[i], 1.2, "score " + (ShamAct)i + " exceeded the documented weight ceiling");
            }
        }

        [Test]
        public void Think_HealthyShamblerNearATree_ChoosesRaidTree()
        {
            TreeEntity tree = world.AddTree(5, 5);
            Shambler s = world.AddShambler(tree.x + 50, tree.y);
            world.game.farmer.x = 40;
            world.game.farmer.y = 40;

            ForceThink(s);

            Assert.AreEqual(ShamAct.RaidTree, s.act);
            Assert.AreSame(tree, s.treeT);
            Assert.Greater(s.scores[(int)ShamAct.RaidTree], 0.0);
        }

        [Test]
        public void Think_HurtShamblerCloseToDanger_ChoosesFlee()
        {
            TreeEntity tree = world.AddTree(5, 5);
            Shambler s = world.AddShambler(tree.x + 30, tree.y);
            s.hp = 1;
            world.game.farmer.x = s.x + 40;
            world.game.farmer.y = s.y;

            ForceThink(s);

            Assert.AreEqual(ShamAct.Flee, s.act);
            Assert.Greater(s.scores[(int)ShamAct.Flee], s.scores[(int)ShamAct.RaidTree],
                "fleeing must outrank a nearby tree when badly hurt and threatened");
        }

        [Test]
        public void Think_FarmerJustHitMeAndIsClose_ChoosesRetaliation()
        {
            Shambler s = world.AddShambler(300, 300);
            world.game.farmer.x = 330;
            world.game.farmer.y = 300;
            world.game.nightTime = 0;
            s.lastHitByFarmer = 0;

            ForceThink(s);

            Assert.AreEqual(ShamAct.HitFarmer, s.act);
            Assert.Greater(s.scores[(int)ShamAct.HitFarmer], 0.0);
        }

        [Test]
        public void HitFarmer_EntersCooldownAfterSwiping()
        {
            Shambler s = world.AddShambler(300, 300);
            world.game.farmer.x = 320;
            world.game.farmer.y = 300;
            world.game.nightTime = 0;
            s.lastHitByFarmer = 0;
            s.act = ShamAct.HitFarmer;

            s.update(0.1);          // swipe, because the farmer is inside 26 px
            ForceThink(s);          // force a fresh decision

            Assert.AreEqual(0.0, s.scores[(int)ShamAct.HitFarmer], 1e-12,
                "the 4 s cooldown must switch retaliation off");
            // The commitment bonus can keep the current action selected while its score is zero;
            // the important invariant is that the cooldown removes the desirability.
        }

        // ---------------- pack behaviour ----------------

        [Test]
        public void PackDog_JoinsWhenAnotherShamblerIsAlreadyCommitted()
        {
            Pip pip = world.game.pip;
            pip.x = 400;
            pip.y = 300;
            pip.hp = 5;
            Shambler helper = world.AddShambler(410, 300);
            helper.act = ShamAct.PackDog;
            helper.dogT = pip;
            Shambler s = world.AddShambler(390, 300);

            ForceThink(s);

            Assert.AreEqual(ShamAct.PackDog, s.act);
            Assert.AreSame(pip, s.dogT);
            Assert.Greater(s.scores[(int)ShamAct.PackDog], 0.0);
        }

        [Test]
        public void PackDog_DoesNotStartAlone()
        {
            Pip pip = world.game.pip;
            pip.x = 400;
            pip.y = 300;
            Shambler s = world.AddShambler(390, 300);

            ForceThink(s);

            Assert.AreNotEqual(ShamAct.PackDog, s.act);
            Assert.Less(s.scores[(int)ShamAct.PackDog], 0.12,
                "without company the pack score must stay below the commitment bonus");
        }

        [Test]
        public void Committed_CountsOnlyOtherShamblersAimingAtThatDog()
        {
            Pip pip = world.game.pip;
            Shambler a = world.AddShambler(300, 300);
            Shambler b = world.AddShambler(320, 300);
            a.act = ShamAct.PackDog;
            a.dogT = pip;

            Assert.AreEqual(1, Shambler.Committed(pip, b));
            Assert.AreEqual(0, Shambler.Committed(pip, a), "a shambler does not count itself");
            Assert.AreEqual(0, Shambler.Committed(world.game.thistle, b));
        }

        [Test]
        public void DogHp_ReadsTheRealDogHealth()
        {
            world.game.pip.hp = 3.5;
            world.game.thistle.hp = 1.5;

            Assert.AreEqual(3.5, Shambler.DogHp(world.game.pip), 1e-12);
            Assert.AreEqual(1.5, Shambler.DogHp(world.game.thistle), 1e-12);
            Assert.AreEqual(0.0, Shambler.DogHp(world.game.farmer), 1e-12);
        }

        // ---------------- fruit ----------------

        [Test]
        public void EatFruit_HealsThreeAndConsumesTheBait()
        {
            world.game.trees.Clear();
            Shambler s = world.AddShambler(300, 300);
            s.hp = 2;
            world.AddBait(300, 300);

            ForceThink(s);

            Assert.AreEqual(ShamAct.EatFruit, s.act);
            Assert.AreEqual(0, world.game.baits.Count, "the bait must be eaten");
            Assert.AreEqual(5.0, s.hp, 1e-9, "2 + 3 healing");
        }

        [Test]
        public void EatFruit_NeverExceedsMaxHealth()
        {
            world.game.trees.Clear();
            Shambler s = world.AddShambler(300, 300);
            s.hp = 5;
            world.AddBait(300, 300);

            ForceThink(s);

            Assert.AreEqual(0, world.game.baits.Count);
            Assert.AreEqual(Shambler.MAX_HP, s.hp, 1e-9);
        }

        [Test]
        public void EatFruit_TargetThatDisappears_FallsBackToRaidTree()
        {
            world.game.trees.Clear();
            Shambler s = world.AddShambler(300, 300);
            s.act = ShamAct.EatFruit;
            s.fruitT = new Bait(500, 300);   // remembered target that is no longer in G.baits
            s.hp = 4;

            s.update(0.01);     // bait is gone before the next think

            Assert.AreEqual(ShamAct.RaidTree, s.act);
        }

        // ---------------- raid and whirl ----------------

        [Test]
        public void RaidTree_ChewsTenDamagePerSecond()
        {
            TreeEntity tree = world.AddTree(5, 5);
            Shambler s = world.AddShambler(tree.x + 20, tree.y);
            s.act = ShamAct.RaidTree;
            s.treeT = tree;
            double before = tree.hp;

            s.update(1.0);

            Assert.AreEqual(before - 10.0, tree.hp, 0.01);
            Assert.IsTrue(tree.alive);
        }

        [Test]
        public void Think_PicksRaidTree_WhenHealthyAndTreeUnattended()
        {
            Shambler s = world.AddShambler(300, 300);
            world.AddTree(U.w2c(340, 300).cx, U.w2c(340, 300).cy);
            ForceThink(s);

            Assert.AreEqual(ShamAct.RaidTree, s.act);
            Assert.Greater(s.scores[(int)ShamAct.RaidTree], 0.0);
        }

        [Test]
        public void Think_PicksFlee_WhenSeverelyInjured()
        {
            Shambler s = world.AddShambler(300, 300);
            s.hp = 0.5;
            world.game.farmer.x = 310;
            world.game.farmer.y = 300;
            ForceThink(s);

            Assert.AreEqual(ShamAct.Flee, s.act);
            Assert.Greater(s.scores[(int)ShamAct.Flee], s.scores[(int)ShamAct.RaidTree]);
        }

        [Test]
        public void Think_PicksEatFruit_WhenHurtAndFruitNearby()
        {
            Shambler s = world.AddShambler(300, 300);
            s.hp = 2.0;
            world.AddBait(310, 300);
            world.game.farmer.x = 900;
            world.game.farmer.y = 900;
            ForceThink(s);

            Assert.AreEqual(ShamAct.EatFruit, s.act);
            Assert.Greater(s.scores[(int)ShamAct.EatFruit], 0.0);
        }

        [Test]
        public void Think_PicksHitFarmer_WhenHitRecentlyByFarmer()
        {
            Shambler s = world.AddShambler(300, 300);
            world.game.farmer.x = 320;
            world.game.farmer.y = 300;
            world.game.nightTime = 10;
            s.lastHitByFarmer = 9.8;
            ForceThink(s);

            Assert.AreEqual(ShamAct.HitFarmer, s.act);
            Assert.Greater(s.scores[(int)ShamAct.HitFarmer], 0.0);
        }

        [Test]
        public void Think_PackDog_ScoresHigherWhenPartnerShamblerAssists()
        {
            Shambler a = world.AddShambler(300, 300);
            world.game.pip.x = 320;
            world.game.pip.y = 300;
            ForceThink(a);
            double soloScore = a.scores[(int)ShamAct.PackDog];

            Shambler b = world.AddShambler(310, 300);
            b.biting = true;
            b.dogT = world.game.pip;
            ForceThink(a);
            double assistedScore = a.scores[(int)ShamAct.PackDog];

            Assert.Greater(assistedScore, soloScore, "a partner already biting boosts pack desire");
        }

        [Test]
        public void CommitmentBonus_KeepsCurrentAction_WhenScoreIsClose()
        {
            Shambler s = world.AddShambler(300, 300);
            s.act = ShamAct.RaidTree;
            ForceThink(s);
            // Current action receives +0.12 commitment bonus
            Assert.AreEqual(ShamAct.RaidTree, s.act);
        }

        [Test]
        public void Perception_BlocksTargeting_ThroughHills()
        {
            Shambler s = world.AddShambler(100, 100);
            Cell c = U.w2c(150, 100);
            world.game.grid[c.cx, c.cy] = C.HILL;
            world.game.pip.x = 200;
            world.game.pip.y = 100;

            Assert.IsFalse(Perception.LineOfSight(s, world.game.pip));
        }
    }
}
