using UnityEngine;

namespace EasyGit.Editor.View
{
    public class FontUtils
    {
        private static Font _regularFont; 
        private static Font _solidFont; 
        private static Font _brandsFont;

        public static GUIStyle GetBrandsIconsStyle()
        {
            return new GUIStyle(GUI.skin.button)
            {
                font = LoadBrandsIcons(),
            };
        }
        
        public static GUIStyle GetSolidIconsStyle()
        {
            return new GUIStyle(GUI.skin.button)
            {
                font = LoadSolidIcons(),
            };
        }
        
        public static GUIStyle GetRegularIconsStyle()
        {
            return new GUIStyle(GUI.skin.button)
            {
                font = LoadRegularIcons(),
            };
        }

        public static Font LoadBrandsIcons()    => _brandsFont ??= LoadIcons("Fonts/Brands");
        public static Font LoadSolidIcons()     => _solidFont ??= LoadIcons("Fonts/Solid Pro");
        public static Font LoadRegularIcons()   => _regularFont ??= LoadIcons("Fonts/Regular Pro");

        public static Font LoadIcons(string path) => Resources.Load<Font>(path);
    }
}