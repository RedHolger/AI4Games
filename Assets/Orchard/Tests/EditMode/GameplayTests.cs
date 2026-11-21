using System.Collections.Generic;
using NUnit.Framework;
using Orchard;

namespace Orchard.Tests
{
    // Integration tests for the real night loop, input actions, scoring, pack rule and tornado
    // mechanics in Assets/Orchard/Scripts/OrchardGame.cs and Enemies.cs.
    // Private flow methods are invoked through TestWorld.Call so the production code stays
    // untouched instead of being copied into the test assembly.
    [TestFixture]
    public class GameplayTests
    {
        TestWorld world;
        OrchardGame game;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld(31415);
            world.Fill(C.GRASS);
            game = world.game;
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
        }

        // ---------------- start and night flow ----------------

        [Test]
        public void StartClicked_BuildsTheMap_AndStartsNightOne()
        {
            game.StartClicked();

            Assert.IsTrue(game.running);
            Assert.IsTrue(game.staticBuilt);
            Assert.AreEqual(1, game.night);
            Assert.AreEqual(0.0, game.nightTime, 1e-12);
            Assert.AreEqual(6, game.trees.Count);
            Assert.AreEqual(5, CountType("scatterling"), "night one seeds the ambient yellow flock");
            Assert.AreEqual(7, game.spawnQueue.Count, "5 shamblers + 1 stalker + 1 black-bird flock");
            Assert.IsNotNull(game.farmer);
            Assert.IsNotNull(game.pip);
            Assert.IsNotNull(game.thistle);
            Assert.AreEqual(0, game.baits.Count);
            Assert.AreEqual(0, game.flyingFruit.Count);
            Assert.AreEqual(0, game.fallenBirds.Count);
        }

        [Test]
        public void StartNight_DoesNotGrowTheAmbientFlockBeyondFive()
        {
            for (int i = 0; i < 3; i++) world.AddBird(100 + i * 20, 100, false);
            game.night = 1;

            world.Call("StartNight");

            Assert.AreEqual(5, CountType("scatterling"));
        }

        [Test]
        public void StartNight_ResetsTreesFarmerAndHazards()
        {
            U.SetSeed(5);
            MapGenerator.BuildStatic(game);
            TreeEntity tree = game.trees[0];
            tree.hp = 5;
            tree.fruit = 1;
            game.baits.Add(new Bait(10, 10));
            game.flyingFruit.Add(new FlyingFruit());
            game.fallenBirds.Add(new FallenBird());
            game.night = 1;

            world.Call("StartNight");

            Assert.AreEqual(C.TREE_HP, tree.hp, 1e-12);
            Assert.AreEqual(4, tree.fruit);
            Assert.IsTrue(double.IsPositiveInfinity(tree.firstHit));
            Assert.AreEqual(0, game.baits.Count);
            Assert.AreEqual(0, game.flyingFruit.Count);
            Assert.AreEqual(0, game.fallenBirds.Count);
        }

        [Test]
        public void BuildWave_ScalesWithTheNightNumber_AndSortsSpawnTimes()
        {
            for (int night = 1; night <= 5; night++)
            {
                game.night = night;
                world.Call("BuildWave");

                Assert.AreEqual(4 + night + night + 1, game.spawnQueue.Count, "night " + night);
                int shamblers = 0, stalkers = 0, bad = 0;
                for (int i = 0; i < game.spawnQueue.Count; i++)
                {
                    SpawnEvent e = game.spawnQueue[i];
                    if (e.type == "shambler") { shamblers++; Assert.AreEqual(1, e.n); }
                    else if (e.type == "stalker") { stalkers++; Assert.AreEqual(1, e.n); }
                    else { bad++; Assert.AreEqual("bad", e.type); Assert.AreEqual(4, e.n); }
                    Assert.GreaterOrEqual(e.t, 1.0);
                    Assert.LessOrEqual(e.t, C.NIGHT_LEN);
                    if (i > 0) Assert.LessOrEqual(game.spawnQueue[i - 1].t, e.t);
                }
                Assert.AreEqual(4 + night, shamblers);
                Assert.AreEqual(night, stalkers);
                Assert.AreEqual(1, bad);
            }
        }

        [Test]
        public void EndNight_AdvancesToTheNextNight_WithATransitionCard()
        {
            U.SetSeed(5);
            MapGenerator.BuildStatic(game);
            game.night = 1;
            game.baits.Add(new Bait(10, 10));
            world.AddShambler(300, 300);

            world.Call("EndNight");

            Assert.AreEqual(2, game.night);
            Assert.AreEqual(Phase.Transition, game.phase);
            Assert.IsTrue(game.trans.active);
            Assert.AreEqual(C.TRANS_LEN, game.trans.t, 1e-12);
            StringAssert.Contains("Night 2", game.trans.msg);
            StringAssert.Contains("6 / 6", game.trans.sub);
            Assert.AreEqual(0, game.baits.Count);
            Assert.AreEqual(0, game.enemies.Count, "only the ambient yellow flock survives the night");
        }

        [Test]
        public void EndNight_OnTheFinalNight_WinsTheGame()
        {
            game.night = C.TOTAL_NIGHTS;

            world.Call("EndNight");

            Assert.IsFalse(game.running);
            Assert.IsTrue(game.gameOver);
        }

        [Test]
        public void UpdateGame_AllTreesDead_LosesImmediately()
        {
            U.SetSeed(5);
            MapGenerator.BuildStatic(game);
            foreach (TreeEntity t in game.trees) t.alive = false;

            world.Call("UpdateGame", 0.01);

            Assert.IsFalse(game.running);
            Assert.IsTrue(game.gameOver);
        }

        [Test]
        public void UpdateGame_EndsTheNight_WhenTimeIsUpAndNoGroundEnemiesRemain()
        {
            U.SetSeed(5);
            MapGenerator.BuildStatic(game);
            game.spawnQueue.Clear();
            game.enemies.Clear();
            game.nightTime = C.NIGHT_LEN - 0.005;

            world.Call("UpdateGame", 0.01);

            Assert.AreEqual(2, game.night);
            Assert.AreEqual(Phase.Transition, game.phase);
        }

        [Test]
        public void UpdateGame_ForcesTheNightToEnd_AfterTheGracePeriod()
        {
            U.SetSeed(5);
            MapGenerator.BuildStatic(game);
            game.spawnQueue.Clear();
            game.nightTime = C.NIGHT_LEN + 7.995;
            world.AddShambler(300, 300);

            world.Call("UpdateGame", 0.01);

            Assert.AreEqual(2, game.night);
            Assert.AreEqual(Phase.Transition, game.phase);
        }

        [Test]
        public void Restart_ResetsCountersAndRebuildsTheFirstNight()
        {
            game.fruitSaved = 5;
            game.fruitLost = 2;
            game.fruitFallen = 3;
            game.birdSaved = 1;
            game.revives = 2;
            game.gameOver = true;
            game.staticBuilt = false;

            game.Restart();

            Assert.AreEqual(1, game.night);
            Assert.AreEqual(0, game.fruitSaved);
            Assert.AreEqual(0, game.fruitLost);
            Assert.AreEqual(0, game.fruitFallen);
            Assert.AreEqual(0, game.birdSaved);
            Assert.AreEqual(0, game.revives);
            Assert.IsFalse(game.gameOver);
            Assert.IsTrue(game.running);
            Assert.IsTrue(game.staticBuilt);
            Assert.AreEqual(5, CountType("scatterling"));
            Assert.AreEqual(7, game.spawnQueue.Count);
        }

        // ---------------- farmer actions and scoring ----------------

        [Test]
        public void TryCatch_CollectsTheClosestFlyingFruitInsideCatchRange()
        {
            FlyingFruit near = new FlyingFruit();
            near.x = game.farmer.x + 10;
            near.y = game.farmer.y;
            game.flyingFruit.Add(near);
            FlyingFruit far = new FlyingFruit();
            far.x = game.farmer.x + 200;
            far.y = game.farmer.y;
            game.flyingFruit.Add(far);

            game.TryCatch();

            Assert.AreEqual(1, game.fruitSaved);
            CollectionAssert.DoesNotContain(game.flyingFruit, near);
            CollectionAssert.Contains(game.flyingFruit, far);
        }

        [Test]
        public void TryCatch_OutsideRange_DoesNothing()
        {
            FlyingFruit far = new FlyingFruit();
            far.x = game.farmer.x + C.CATCH_RANGE + 1;
            far.y = game.farmer.y;
            game.flyingFruit.Add(far);

            game.TryCatch();

            Assert.AreEqual(0, game.fruitSaved);
            Assert.AreEqual(1, game.flyingFruit.Count);
        }

        [Test]
        public void TrySwing_HitsOnlyGroundEnemiesInFront()
        {
            game.farmer.faceX = 1;
            game.farmer.faceY = 0;
            Shambler front = world.AddShambler(game.farmer.x + 30, game.farmer.y);
            Stalker behind = world.AddStalker(game.farmer.x - 30, game.farmer.y);
            Stalker outOfRange = world.AddStalker(game.farmer.x + C.SCYTHE_RANGE + 10, game.farmer.y);
            Bird yellow = world.AddBird(game.farmer.x + 30, game.farmer.y + 5, false);

            game.TrySwing();

            Assert.AreEqual(6.0 - C.SCYTHE_DMG, front.hp, 1e-9);
            Assert.AreEqual(6.0, behind.hp, 1e-12);
            Assert.AreEqual(6.0, outOfRange.hp, 1e-12);
            Assert.AreEqual(3.0, yellow.hp, 1e-12, "the scythe cannot hurt the yellow flock");
            Assert.AreEqual(0.32, game.farmer.swing, 1e-9);
        }

        [Test]
        public void TrySwing_PlucksOneFruitFromANearbyTree()
        {
            TreeEntity tree = world.AddTree(5, 5);
            game.farmer.x = tree.x - 40;
            game.farmer.y = tree.y;

            game.TrySwing();

            Assert.AreEqual(3, tree.fruit);
            Assert.AreEqual(1, game.fruitFallen);
            Assert.AreEqual(1, game.flyingFruit.Count);
            Assert.AreEqual(-28.0, game.flyingFruit[0].vy, 1e-12);
            Assert.AreEqual(2.4, game.flyingFruit[0].life, 1e-12);
        }

        [Test]
        public void TrySwing_DoesNothingBeforeTheNightStarts()
        {
            game.running = false;
            TreeEntity tree = world.AddTree(5, 5);
            game.farmer.x = tree.x - 40;
            game.farmer.y = tree.y;

            game.TrySwing();

            Assert.AreEqual(0.0, game.farmer.swing, 1e-12);
            Assert.AreEqual(4, tree.fruit);
        }

        [Test]
        public void FlyingFruit_LandsAsBait_WhenItsLifeExpires()
        {
            world.AddTree(5, 5);
            FlyingFruit f = new FlyingFruit();
            f.x = 120;
            f.y = 140;
            f.life = 0.01;
            game.flyingFruit.Add(f);
            game.spawnQueue.Clear();

            world.Call("UpdateGame", 0.02);

            Assert.AreEqual(0, game.flyingFruit.Count);
            Assert.AreEqual(1, game.baits.Count);
            Assert.AreEqual(120.0, game.baits[0].x, 1e-9);
            Assert.AreEqual(140.0, game.baits[0].y, 1e-9);
        }

        [Test]
        public void GoodBird_EatsBait_AndScoresASavedFruit()
        {
            Bird bird = world.AddBird(300, 300, false);
            world.AddBait(300, 300);

            bird.update(0.01);

            Assert.AreEqual(0, game.baits.Count);
            Assert.AreEqual(1, game.fruitSaved);
            Assert.AreEqual(1, game.birdSaved);
            Assert.AreEqual(0, game.fruitLost);
        }

        [Test]
        public void BadBird_StealsBait_AndScoresALostFruit()
        {
            Bird bird = world.AddBird(300, 300, true);
            world.AddBait(300, 300);

            bird.update(0.01);

            Assert.AreEqual(0, game.baits.Count);
            Assert.AreEqual(0, game.fruitSaved);
            Assert.AreEqual(1, game.fruitLost);
        }

        [Test]
        public void AllTreesDead_EndsTheRun()
        {
            game.running = true;
            game.gameOver = false;
            foreach (TreeEntity t in game.trees) t.alive = false;

            world.Call("UpdateGame", 0.05);

            Assert.IsFalse(game.running);
            Assert.IsTrue(game.gameOver);
        }

        // ---------------- pack rule and farmer stun ----------------

        [Test]
        public void PackBites_NeedsTwoCommittedShamblers_ToHurtADog()
        {
            game.pip.hp = 5;
            Shambler a = world.AddShambler(game.pip.x + 10, game.pip.y);
            Shambler b = world.AddShambler(game.pip.x - 10, game.pip.y);
            a.biting = true; a.dogT = game.pip;
            b.biting = true; b.dogT = game.pip;

            Assert.AreEqual(2, game.PackSize(game.pip));
            world.Call("PackBites", game.pip, 1.0);
            Assert.AreEqual(5.0 - 1.4, game.pip.hp, 1e-9);

            game.pip.hp = 5;
            b.biting = false;
            Assert.AreEqual(1, game.PackSize(game.pip));
            world.Call("PackBites", game.pip, 1.0);
            Assert.AreEqual(5.0, game.pip.hp, 1e-12, "a lone biter cannot hurt a dog");
        }

        [Test]
        public void Farmer_KnockBack_StunsFarmerAndPushesAway()
        {
            game.farmer.stun = 0;
            game.farmer.KnockBack(new Pos(game.farmer.x - 20, game.farmer.y), 150, 0.4);

            Assert.Greater(game.farmer.stun, 0);
            Assert.Greater(game.farmer.kbx, 0);

            game.farmer.update(0.5);
            Assert.LessOrEqual(game.farmer.stun, 0);
        }

        [Test]
        public void Tree_Revive_BringsOldestFallenTreeBack_WhenBankReachesReviveCost()
        {
            TreeEntity tree1 = world.AddTree(5, 5);
            tree1.alive = false; tree1.diedAt = 10;
            TreeEntity tree2 = world.AddTree(8, 8);
            tree2.alive = false; tree2.diedAt = 25;

            game.birdSaved = C.REVIVE_COST;
            Assert.AreEqual(C.REVIVE_COST, game.ReviveBank);

            game.CheckRevive();

            Assert.IsTrue(tree1.alive, "oldest fallen tree revived");
            Assert.IsFalse(tree2.alive, "second dead tree remains dead");
            Assert.AreEqual(1, game.revives);
            Assert.AreEqual(0, game.ReviveBank);
            Assert.AreEqual(tree1.maxhp, tree1.hp, 1e-9);
            Assert.AreEqual(4, tree1.fruit);
        }

        [Test]
        public void Tree_Revive_RevivesMultipleTrees_WithSufficientBank()
        {
            TreeEntity tree1 = world.AddTree(5, 5);
            tree1.alive = false; tree1.diedAt = 5;
            TreeEntity tree2 = world.AddTree(8, 8);
            tree2.alive = false; tree2.diedAt = 15;

            game.birdSaved = C.REVIVE_COST * 2;
            game.CheckRevive();

            Assert.IsTrue(tree1.alive);
            Assert.IsTrue(tree2.alive);
            Assert.AreEqual(2, game.revives);
        }

        // ---------------- trees and birds ----------------

        [Test]
        public void Tree_GnawDestroysIt_AndHealClampsAtMax()
        {
            TreeEntity tree = world.AddTree(5, 5);
            tree.gnaw(1000, 1);
            Assert.IsFalse(tree.alive);
            Assert.LessOrEqual(tree.hp, 0);

            tree.heal(1000, 1);
            Assert.LessOrEqual(tree.hp, 0, "a dead tree cannot be healed");

            TreeEntity healthy = world.AddTree(8, 5);
            healthy.hp = 10;
            healthy.heal(1000, 1);
            Assert.AreEqual(healthy.maxhp, healthy.hp, 1e-12);
        }

        [Test]
        public void Tree_EatFruitStopsAtZero()
        {
            TreeEntity tree = world.AddTree(5, 5);

            for (int i = 0; i < 4; i++) Assert.IsTrue(tree.eatFruit());
            Assert.IsFalse(tree.eatFruit());
            Assert.AreEqual(0, tree.fruit);
        }

        [Test]
        public void FallenBird_TumblesAndLandsOnGround()
        {
            FallenBird fb = new FallenBird { h = 50, vh = 0, spin = 2, landed = false };
            game.fallenBirds.Add(fb);

            world.Call("UpdateGame", 1.0);

            Assert.IsTrue(fb.landed);
            Assert.AreEqual(0.0, fb.h, 1e-9);
            Assert.AreEqual(System.Math.PI, fb.rot, 1e-9);
        }

        [Test]
        public void BirdCaught_SpawnsFallenBird_AndBadBirdRests()
        {
            Bird bad = world.AddBird(300, 300, true);
            Bird good = world.AddBird(300, 300, false);

            bad.update(0.01);

            Assert.IsTrue(good.dead);
            Assert.AreEqual(1, game.fallenBirds.Count);
            Assert.AreEqual(C.KILL_COOLDOWN, bad.killCd, 1e-9);
        }

        // ---------------- spawns and logging ----------------

        [Test]
        public void EdgePos_ReturnsOpenReachableEdgeCells()
        {
            U.SetSeed(5);
            MapGenerator.BuildStatic(game);

            for (int i = 0; i < 50; i++)
            {
                Pos p = (Pos)world.Call("EdgePos");
                Cell c = U.w2c(p.x, p.y);
                Assert.IsTrue(U.inB(c.cx, c.cy));
                bool edge = c.cx == 0 || c.cy == 0 || c.cx == C.GW - 1 || c.cy == C.GH - 1;
                Assert.IsTrue(edge, "spawn point must be on the board edge");
                Assert.IsFalse(U.groundBlocked(c.cx, c.cy));
                Assert.IsTrue(game.reach[c.cx, c.cy], "spawn point must join the orchard");
            }
        }

        [Test]
        public void SpawnActors_PlacesTheFarmerAndDogsAtTheCentre()
        {
            game.SpawnActors();
            Pos centre = U.cc(C.GW >> 1, C.GH >> 1);

            Assert.AreEqual(centre.x, game.farmer.x, 1e-12);
            Assert.AreEqual(centre.y, game.farmer.y, 1e-12);
            Assert.AreEqual(centre.x - C.CELL, game.pip.x, 1e-12);
            Assert.AreEqual(centre.x + C.CELL, game.thistle.x, 1e-12);
            Assert.AreEqual(centre.y, game.pip.y, 1e-12);
            Assert.AreEqual(centre.y, game.thistle.y, 1e-12);
        }

        [Test]
        public void LogOnce_SuppressesTheSameKeyForThreeSeconds()
        {
            game.nightTime = 0;
            game.LogOnce("test", "first");
            game.LogOnce("test", "second");

            Assert.AreEqual(1, game.logLines.Count);

            game.nightTime = 4;
            game.LogOnce("test", "third");

            Assert.AreEqual(2, game.logLines.Count);
        }

        int CountType(string type)
        {
            int n = 0;
            foreach (Enemy e in game.enemies) if (e.type == type) n++;
            return n;
        }
    }
}
