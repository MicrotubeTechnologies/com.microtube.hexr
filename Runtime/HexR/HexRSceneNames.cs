using UnityEngine.SceneManagement;

namespace HexR
{
    /// <summary>
    /// How a build scene is named to a person: the floating menu's scene buttons and the scene list
    /// HexR Remote sends to a phone read the same.
    /// </summary>
    internal static class HexRSceneNames
    {
        /// <summary>
        /// "Assets/Scenes/2.Hospital Tutorial.unity" reads as "2.Hospital Tutorial", and a file name
        /// written in one word reads as words: "LivingHeart" as "Living Heart", "FindTheLump" as
        /// "Find the Lump".
        /// </summary>
        public static string DisplayName(int buildIndex)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(buildIndex);
            if (string.IsNullOrEmpty(path))
            {
                return "Scene " + buildIndex;
            }

            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            int start = slash + 1;
            int length = (dot > start ? dot : path.Length) - start;
            return Words(path.Substring(start, length));
        }

        internal static string Words(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf(' ') >= 0)
            {
                return name;
            }

            System.Text.StringBuilder spaced = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
                {
                    spaced.Append(' ');
                }

                spaced.Append(name[i]);
            }

            string[] words = spaced.ToString().Split(' ');
            for (int i = 1; i < words.Length; i++)
            {
                string w = words[i];
                if (w == "The" || w == "A" || w == "Of" || w == "And")
                {
                    words[i] = w.ToLowerInvariant();
                }
            }

            return string.Join(" ", words);
        }
    }
}
