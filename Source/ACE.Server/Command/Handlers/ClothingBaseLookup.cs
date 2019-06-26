using ACE.Database.Models.Shard;
using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ACE.Server.Command.Handlers
{
    class ClothingBaseLookup
    {
        // Key is the setup, List<uint> is the ClothingTable entries for that setup
        Dictionary<uint, List<uint>> Lookup;
        uint CurrentSetup;
        uint CurrentClothingBase;

        public ClothingBaseLookup()
        {
            Lookup = new Dictionary<uint, List<uint>>();

            // on create, build an index of setup to available ClothingBase entries. Do this just the once and re-use the class, because otherwise, ugh, time.
            foreach (var kvp in DatManager.PortalDat.AllFiles)
            {
                if (kvp.Key == 0xFFFF0001) // Not sure what this is, EOF record maybe?
                    continue;

                var fileType = kvp.Value.GetFileType(DatDatabaseType.Portal);
                if (fileType == DatFileType.Clothing)
                {
                    var clothingBase = (uint)kvp.Value.ObjectId;
                    ClothingTable item = DatManager.PortalDat.ReadFromDat<ClothingTable>(clothingBase);

                    foreach (var entry in item.ClothingBaseEffects)
                    {
                        var setup = entry.Key;
                        if (!Lookup.ContainsKey(setup))
                            Lookup[setup] = new List<uint>();

                        Lookup[setup].Add(clothingBase);
                    }
                }
            }
        }

        private List<uint> getClothingBase(uint setup)
        {
            if (Lookup.ContainsKey(setup))
                return Lookup[setup];
            else
                return new List<uint>();
        }

        // returns a Dictionary of ClothingBase (key) and matching PaletteTemplates (value), if any found
        public List<(uint ClothingBase, int PaletteTemplate)> DoSearch(Biota biota)
        {
           var matches = new List<(uint ClothingBase, int PaletteTemplate)>();

            CurrentSetup = biota.GetProperty(PropertyDataId.Setup) ?? 0;
            uint setup = CurrentSetup;
            //ObjDesc objdesc = wo.ObjD

            // HARD CODE SOME TESTING HERE!
            //Lookup[setup] = new List<uint>();
            //Lookup[setup].Add(0x1000072B);

            if (Lookup.ContainsKey(setup))
            {
                var myModelsList = GetModelsFromBiota(biota);
                var myTexturesList = GetTexturesFromBiota(biota);
                var myTexturesListCopy = GetTexturesFromBiota(biota);
                var myPalettesList = GetSubPalsFromBiota(biota);

                for (int i = 0; i < Lookup[setup].Count; i++)
                {
                    CurrentClothingBase = Lookup[setup][i];

                    ClothingTable clothing = DatManager.PortalDat.ReadFromDat<ClothingTable>(Lookup[setup][i]);
                    bool modelsMatch = CompareModels(clothing.ClothingBaseEffects[setup].CloObjectEffects, myModelsList);

                    // Check the texture data
                    bool texturesMatch = true;
                    if (modelsMatch == true)
                        texturesMatch = CompareTextures(clothing.ClothingBaseEffects[setup].CloObjectEffects, myTexturesList, myTexturesListCopy);

                    bool palettesMatch = true;
                    int paletteTemplate = -1;
                    if (modelsMatch && texturesMatch)
                    {
                        paletteTemplate = ComparePalettes(clothing.ClothingSubPalEffects, myPalettesList);
                        if (paletteTemplate == -1)
                            palettesMatch = false;
                    }

                    if (modelsMatch && texturesMatch && palettesMatch)
                    {
                        matches.Add((CurrentClothingBase, paletteTemplate));
                    }
                }

            }

            return matches;
        }

        private bool CompareModels(List<CloObjectEffect> objEffects, Dictionary<uint, uint> models) {
            if(models.Count == 0)
                return true;
            if (models.Count != objEffects.Count)
                return false;

            foreach(CloObjectEffect entry in objEffects)
            {
                uint partNum = entry.Index;
                uint objectId = entry.ModelId;
                if (models.ContainsKey(partNum) && models[partNum] == objectId)
                    models.Remove(partNum);
            }

            if (models.Count > 0)
                return false;

            return true;
        }

        private bool CompareTextures(List<CloObjectEffect> cloObjectEffects, Dictionary<uint, Dictionary<uint, TextureMapChange>> textures, Dictionary<uint, Dictionary<uint, TextureMapChange>> texturesCopy)
        {
            if (textures.Count == 0 && cloObjectEffects.Count == 0)
                return true;

            foreach (CloObjectEffect obj in cloObjectEffects)
            {
                if(obj.CloTextureEffects.Count > 0)
                {
                    uint partNum = obj.Index;
                    foreach(CloTextureEffect texEffect in obj.CloTextureEffects)
                    {
                        var oldTexId = texEffect.OldTexture;
                        var newTexID = texEffect.NewTexture;

                        if (textures.ContainsKey(partNum))
                        {
                            //var texPart = textures[partNum]; // a duplicate we can modify without breaking things
                            Dictionary<uint, TextureMapChange> texPart = new Dictionary<uint, TextureMapChange>();
                            foreach(var kvp in textures[partNum])
                            {
                                texPart.Add(kvp.Key, kvp.Value);
                            }

                            foreach (var entry in textures[partNum])
                            {
                                if (entry.Value.NewTexture == newTexID && entry.Value.OldTexture == oldTexId)
                                    texPart.Remove(entry.Key);
                            }
                            // if the remaining parts is empty, removing the key from textures
                            if (texPart.Count == 0)
                                texturesCopy.Remove(partNum);
                            else
                                texturesCopy[partNum] = texPart; // Put what IS remaining back into that entry

                        }

                        textures = texturesCopy;
                    }
                }
            }

            if (textures.Count > 0)
                return false;

            return true;
        }

        private int ComparePalettes(Dictionary<uint, CloSubPalEffect> subPalEffects, List<SubPalette> palettes)
        {
            if (palettes.Count > 0 && subPalEffects.Count == 0)
                return -1;

            foreach(var entry in subPalEffects)
            {

                Dictionary<int, SubPalette> palComp = new Dictionary<int, SubPalette>(); // copy this so we can modify it to see how we're doing!
                for (var j = 0; j < palettes.Count; j++)
                {
                    palComp.Add(j, palettes[j]);
                }
                uint palTemplate = entry.Key;

                CloSubPalEffect obj = entry.Value;
                if(obj.CloSubPalettes.Count > 0)
                {
                    foreach(var ranges in obj.CloSubPalettes)
                    {
                        if (ranges.Ranges.Count > 0)
                        {
                            uint palSetId = ranges.PaletteSet;
                            PaletteSet palSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(palSetId);

                            for(var i = 0;i<ranges.Ranges.Count; i++)
                            {
                                for(var j = 0; j < palettes.Count; j++)
                                {
                                    if(ranges.Ranges[i].Offset == palettes[j].Offset && ranges.Ranges[i].NumColors == palettes[j].NumColors && palSet.PaletteList.IndexOf(palettes[j].SubID) != -1 )
                                    {
                                        palComp.Remove(j);
                                    }

                                }
                            }
                        }

                    }
                }

                if (palComp.Count == 0)
                    return (int)palTemplate;
            }

            return -1;
        }

        private Dictionary<uint, uint> GetModels(string modelsString)
        {
            Dictionary<uint, uint> models = new Dictionary<uint, uint>();

            List<string> modelsList = modelsString.Split("|").ToList();
            for (int i = 0; i < modelsList.Count; i++)
            {
                List<string> modelsTemp = modelsList[i].Split("/").ToList();
                if (modelsTemp.Count >= 2)
                {
                    uint partID = uint.Parse(modelsTemp[0]);
                    uint part = uint.Parse(modelsTemp[1]);

                    models.Add(partID, part);
                }
            }
            return models;
        }

        private Dictionary<uint, uint> GetModelsFromBiota(Biota biota)
        {
            Dictionary<uint, uint> models = new Dictionary<uint, uint>();
            
            if(biota.BiotaPropertiesAnimPart.Count > 0)
            foreach (var animPart in biota.BiotaPropertiesAnimPart)
                models.Add(animPart.Index, animPart.AnimationId);

            return models;
        }

        private List<SubPalette> GetSubPals(string srcString)
        {
            List<SubPalette> subPals = new List<SubPalette>();

            List<string> rawList = srcString.Split("|").ToList();
            for (int i = 0; i < rawList.Count; i++)
            {
                List<string> rawTemp = rawList[i].Split("/").ToList();
                if (rawTemp.Count >= 3)
                {
                    uint palID = uint.Parse(rawTemp[0]);
                    uint offset = uint.Parse(rawTemp[1]) * 8;
                    uint colors = uint.Parse(rawTemp[2]) * 8;
                    if (colors == 0) colors = 2048;
                    SubPalette pal = new SubPalette();
                    pal.SubID = palID;
                    pal.Offset = offset;
                    pal.NumColors = colors;
                    subPals.Add(pal);
                }
            }
            return subPals;
        }

        private List<SubPalette> GetSubPalsFromBiota(Biota biota)
        {
            List<SubPalette> subPals = new List<SubPalette>();

            if(biota.BiotaPropertiesPalette.Count > 0)
            {
                foreach( var subPal in biota.BiotaPropertiesPalette)
                {
                    uint palID = subPal.SubPaletteId;
                    uint offset = (uint)subPal.Offset * 8;
                    uint colors = (uint)subPal.Length * 8;
                    if (colors == 0) colors = 2048;
                    SubPalette pal = new SubPalette();
                    pal.SubID = palID;
                    pal.Offset = offset;
                    pal.NumColors = colors;
                    subPals.Add(pal);
                }
            }
            return subPals.OrderBy(o=>o.SubID).ToList();
        }

        private Dictionary<uint, Dictionary<uint, TextureMapChange>> GetTexturesFromBiota(Biota biota)
        {
            Dictionary<uint, Dictionary<uint, TextureMapChange>> textures = new Dictionary<uint, Dictionary<uint, TextureMapChange>>();

            if (biota.BiotaPropertiesTextureMap.Count > 0)
            {
                foreach( var texChange in biota.BiotaPropertiesTextureMap)
                {
                    uint partID = texChange.Index;
                    uint oldTexID = texChange.OldId;
                    uint newTexID = texChange.NewId;
                    TextureMapChange tex = new TextureMapChange();
                    tex.PartIndex = Convert.ToByte(partID);
                    tex.OldTexture = oldTexID;
                    tex.NewTexture = newTexID;

                    if (textures.ContainsKey(partID))
                    {
                        Dictionary<uint, TextureMapChange> textureMapChangeList = textures[partID];
                        textureMapChangeList.Add((uint)textureMapChangeList.Count, tex);
                        textures[partID] = textureMapChangeList;
                    }
                    else
                    {
                        Dictionary<uint, TextureMapChange> textureMapChangeList = new Dictionary<uint, TextureMapChange>();
                        textureMapChangeList.Add(0, tex);
                        textures.Add(partID, textureMapChangeList);
                    }
                }
            }

            return textures;
        }

        public double? GetShade(Biota biota, uint clothingBase, int paletteTemplate)
        {
            ClothingTable clothing = DatManager.PortalDat.ReadFromDat<ClothingTable>(clothingBase);

            if (!clothing.ClothingSubPalEffects.TryGetValue((uint)paletteTemplate, out var subPal))
                return null;

            // get all the paletteIds from the palettes
            List<uint> palIds = new List<uint>();
            foreach (var pal in biota.BiotaPropertiesPalette)
                if (!palIds.Contains(pal.SubPaletteId))
                    palIds.Add(pal.SubPaletteId);

            // find the biggest PaletteSet and calculate the shade based off of that.
            // Should result in the most precise shade value.
            PaletteSet maxPalSet = null;
            for (var i = 0; i < subPal.CloSubPalettes.Count; i++)
            {
                var palSetId = subPal.CloSubPalettes[i].PaletteSet;
                PaletteSet palSet = DatManager.PortalDat.ReadFromDat<PaletteSet>(palSetId);
                if (maxPalSet == null || palSet.PaletteList.Count > maxPalSet.PaletteList.Count)
                    maxPalSet = palSet;
            }

            if (maxPalSet == null)
                return null;

            for (var i = 0; i < palIds.Count; i++)
            {
                if (maxPalSet.PaletteList.Contains(palIds[i]))
                    return CalcShade(palIds[i], maxPalSet);
            }


            return null;
        }

        private double? CalcShade(uint palId, PaletteSet palSet)
        {
            if (palSet.PaletteList.Count == 1) return 0; // Only one palette in the set, then it's 0...easy

            // get the paletteIndex
            var palIdx = palSet.PaletteList.IndexOf(palId);
            if (palIdx != -1)
            {
                double shade = (palIdx + 1) / (palSet.PaletteList.Count - 0.0000001); // the +1 is because this is a 0-index list
                return shade;
            }
            return null;
        }
    }
}
