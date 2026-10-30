using HaptGlove;
using NUnit.Framework;

namespace HexR.Tests
{
    /// <summary>
    /// Every arm module's own name, from its address. The reference names are the HEXR Companion's
    /// (desktop tests/test_glovenames.py, Android GloveNamesTest.kt): every HexR app and Companion has
    /// to give a module the same name. Change one, and every module in the field is renamed.
    /// </summary>
    public class GloveNamesTests
    {
        [TestCase("D4:3D:39:A1:B2:C3", "Fiery Panther", "火焰黑豹")]
        [TestCase("D4:3D:39:D4:E5:F6", "Velvet Finch", "丝绒雀鸟")]
        [TestCase("00:00:00:00:00:00", "Swift Finch", "飞速雀鸟")]
        [TestCase("FF:FF:FF:FF:FF:FF", "Swift Squirrel", "飞速松鼠")]
        [TestCase("C8:F0:9E:12:34:56", "Zesty Kiwi", "热情几维鸟")]
        public void Names_MatchTheReference_EveryCompanionUses(string address, string english, string chinese)
        {
            Assert.AreEqual(english, GloveNames.For(address));
            Assert.AreEqual(chinese, GloveNames.For(address, chinese: true));
        }

        [Test]
        public void TheWayAnAddressIsWritten_DoesNotChangeTheName()
        {
            Assert.AreEqual("Fiery Panther", GloveNames.For("d4:3d:39:a1:b2:c3"));
            Assert.AreEqual("Fiery Panther", GloveNames.For("D43D39A1B2C3"));
            Assert.AreEqual("", GloveNames.For(null));
        }
    }
}
