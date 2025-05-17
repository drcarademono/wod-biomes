using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using System.Collections;
using UnityEngine;

namespace WorldOfDaggerfall
{
    public class WODBiomes : MonoBehaviour
    {
        public static WODBiomes instance;
        public static Mod Mod { get; private set; }

	    private static Mod VEMod;
	    public static bool VEModEnabled;

        #region Invoke
        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            Mod = initParams.Mod;
            var go = new GameObject(Mod.Title);
            instance = go.AddComponent<WODBiomes>();

		    VEMod = ModManager.Instance.GetModFromGUID("1f124f8c-dd01-48ad-a5b9-0b4a0e4702d2");
		    if (VEMod != null && VEMod.Enabled)
		    {
			    VEModEnabled = true;
		    }
        }
        #endregion

        private void Start()
        {
            // Set WOD custom terrain material provider
            if(WODTilemapTextureArrayTerrainMaterialProvider.IsSupported)
                DaggerfallUnity.Instance.TerrainMaterialProvider = new WODTilemapTextureArrayTerrainMaterialProvider();
            else                 
                DaggerfallUnity.Instance.TerrainMaterialProvider = new WODTilemapTerrainMaterialProvider();
        }
    }
}

