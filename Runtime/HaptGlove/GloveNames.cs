using System;

namespace HaptGlove
{
    /// <summary>
    /// A name for every arm module, worked out from its Bluetooth address alone.
    ///
    /// Every left glove advertises the same name, and so does every right one, so the only thing
    /// that tells two arm modules apart is the address, which nobody can remember. This turns the
    /// address into an adjective, an animal and a number ("Fiery Panther 3"), the same way
    /// everywhere: every HexR app and every HEXR Companion (desktop hexr/glovenames.py, Android
    /// GloveNames.kt) names the same module the same, with nothing shared between them and nothing
    /// to set up.
    ///
    /// The recipe:
    ///  1. the address's hex digits, upper case, nothing else ("D4:3D:39:A1:B2:C3" -> "D43D39A1B2C3");
    ///  2. their FNV-1a 32-bit hash (offset basis 2166136261, prime 16777619, over the ASCII bytes);
    ///  3. adjective = Adjectives[hash % 64], animal = Animals[(hash >> 6) % 64],
    ///     number = (hash >> 12) % 10 + 1;
    ///  4. "Adjective Animal N" in English, 形容词动物N号 in Chinese.
    /// The lists are part of the recipe: never reorder or change an entry, or every module gets a
    /// new name. GloveNamesTests holds the reference names all three are tested against. 40960
    /// names: about a 0.1% chance that 10 modules have two alike, 3% for 50.
    /// </summary>
    public static class GloveNames
    {
        // (English, Chinese), in this order for good.
        private static readonly string[,] Adjectives =
        {
            { "Amber", "琥珀" }, { "Azure", "蔚蓝" }, { "Bold", "大胆" }, { "Brave", "勇敢" },
            { "Bright", "明亮" }, { "Brisk", "轻快" }, { "Calm", "沉静" }, { "Clever", "聪明" },
            { "Coral", "珊瑚" }, { "Cosmic", "星际" }, { "Crimson", "深红" }, { "Daring", "无畏" },
            { "Dusky", "暮色" }, { "Eager", "热切" }, { "Fiery", "火焰" }, { "Frosty", "霜白" },
            { "Gentle", "温和" }, { "Golden", "金色" }, { "Happy", "快乐" }, { "Hazel", "榛色" },
            { "Honest", "诚实" }, { "Indigo", "靛蓝" }, { "Ivory", "象牙" }, { "Jade", "翡翠" },
            { "Jolly", "欢乐" }, { "Keen", "敏锐" }, { "Kind", "善良" }, { "Lively", "活泼" },
            { "Lucky", "幸运" }, { "Lunar", "月光" }, { "Merry", "愉快" }, { "Mighty", "强大" },
            { "Misty", "薄雾" }, { "Noble", "高贵" }, { "Olive", "橄榄" }, { "Plucky", "机灵" },
            { "Polar", "极地" }, { "Proud", "骄傲" }, { "Quick", "敏捷" }, { "Quiet", "安静" },
            { "Rapid", "迅疾" }, { "Royal", "皇家" }, { "Ruby", "红宝石" }, { "Rusty", "锈色" },
            { "Sandy", "沙色" }, { "Scarlet", "猩红" }, { "Silver", "银色" }, { "Snowy", "雪白" },
            { "Solar", "太阳" }, { "Spry", "矫健" }, { "Steady", "稳健" }, { "Stormy", "风暴" },
            { "Sunny", "阳光" }, { "Swift", "飞速" }, { "Teal", "青色" }, { "Tidy", "整洁" },
            { "Velvet", "丝绒" }, { "Vivid", "鲜艳" }, { "Witty", "风趣" }, { "Wild", "狂野" },
            { "Windy", "疾风" }, { "Zesty", "热情" }, { "Cheery", "开朗" }, { "Sleek", "流线" },
        };

        private static readonly string[,] Animals =
        {
            { "Otter", "水獭" }, { "Falcon", "猎鹰" }, { "Panda", "熊猫" }, { "Tiger", "老虎" },
            { "Koala", "考拉" }, { "Lynx", "猞猁" }, { "Heron", "苍鹭" }, { "Bison", "野牛" },
            { "Badger", "獾" }, { "Beaver", "河狸" }, { "Camel", "骆驼" }, { "Cobra", "眼镜蛇" },
            { "Crane", "仙鹤" }, { "Dingo", "澳洲野犬" }, { "Dolphin", "海豚" }, { "Eagle", "雄鹰" },
            { "Ferret", "雪貂" }, { "Finch", "雀鸟" }, { "Fox", "狐狸" }, { "Gecko", "壁虎" },
            { "Gibbon", "长臂猿" }, { "Hare", "野兔" }, { "Hawk", "鹰" }, { "Hedgehog", "刺猬" },
            { "Ibis", "朱鹭" }, { "Jaguar", "美洲豹" }, { "Kestrel", "红隼" }, { "Kiwi", "几维鸟" },
            { "Lemur", "狐猴" }, { "Leopard", "花豹" }, { "Lion", "狮子" }, { "Llama", "羊驼" },
            { "Magpie", "喜鹊" }, { "Marten", "貂" }, { "Moose", "驼鹿" }, { "Narwhal", "独角鲸" },
            { "Newt", "蝾螈" }, { "Ocelot", "豹猫" }, { "Orca", "虎鲸" }, { "Osprey", "鱼鹰" },
            { "Owl", "猫头鹰" }, { "Panther", "黑豹" }, { "Parrot", "鹦鹉" }, { "Pelican", "鹈鹕" },
            { "Penguin", "企鹅" }, { "Puffin", "海鹦" }, { "Puma", "美洲狮" }, { "Quail", "鹌鹑" },
            { "Rabbit", "兔子" }, { "Raven", "渡鸦" }, { "Robin", "知更鸟" }, { "Salmon", "鲑鱼" },
            { "Seal", "海豹" }, { "Shark", "鲨鱼" }, { "Sparrow", "麻雀" }, { "Squirrel", "松鼠" },
            { "Swan", "天鹅" }, { "Tapir", "貘" }, { "Toucan", "巨嘴鸟" }, { "Turtle", "海龟" },
            { "Walrus", "海象" }, { "Whale", "鲸鱼" }, { "Wolf", "狼" }, { "Yak", "牦牛" },
        };

        /// <summary>The arm module's own name, from its address: "Fiery Panther 3", or "火焰黑豹3号" in Chinese. Empty for no address.</summary>
        public static string For(string address, bool chinese = false)
        {
            if (string.IsNullOrEmpty(address))
            {
                return "";
            }

            uint h = 2166136261u;
            foreach (char c in address.ToUpperInvariant())
            {
                if ((c < '0' || c > '9') && (c < 'A' || c > 'F'))
                {
                    continue;
                }

                unchecked
                {
                    h = (h ^ c) * 16777619u;
                }
            }

            int column = chinese ? 1 : 0;
            string adjective = Adjectives[h % 64, column];
            string animal = Animals[(h >> 6) % 64, column];
            uint number = (h >> 12) % 10 + 1;
            return chinese ? adjective + animal + number + "号" : adjective + " " + animal + " " + number;
        }
    }
}
