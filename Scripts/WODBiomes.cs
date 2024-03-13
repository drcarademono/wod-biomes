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

        #region Invoke
        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            Mod = initParams.Mod;
            var go = new GameObject(Mod.Title);
            instance = go.AddComponent<WODBiomes>();
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

