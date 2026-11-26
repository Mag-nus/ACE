using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

static class Stubs
{
    static void SetBacking(object o, Type t, string prop, object value)
    {
        var f = t.GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (f == null) throw new Exception("no backing field " + prop + " on " + t);
        f.SetValue(o, value);
    }

    /// Builds a bare WorldObject with a Biota, guid and Location, no PhysicsObj, no dat/db.
    public static WorldObject MakeWorldObject(uint guid = 0x80001234)
    {
        var wo = new StubWO();
        var biota = new Biota { Id = guid, WeenieClassId = 1, WeenieType = WeenieType.Generic };
        biota.PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int>();
        biota.PropertiesPosition = new Dictionary<ACE.Entity.Enum.Properties.PositionType, ACE.Entity.Models.PropertiesPosition>();
        biota.PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>();
        biota.PropertiesBool = new Dictionary<ACE.Entity.Enum.Properties.PropertyBool, bool>();
        biota.PropertiesFloat = new Dictionary<ACE.Entity.Enum.Properties.PropertyFloat, double>();
        biota.PropertiesDID = new Dictionary<ACE.Entity.Enum.Properties.PropertyDataId, uint>();
        biota.PropertiesIID = new Dictionary<ACE.Entity.Enum.Properties.PropertyInstanceId, uint>();
        SetBacking(wo, typeof(WorldObject), "Biota", biota);
        SetBacking(wo, typeof(WorldObject), "Guid", new ObjectGuid(guid));
        // A moving object in Holtburg: rotation with all four components non-zero (worst case, 16 bytes of quaternion)
        wo.Location = new Position(0xA9B40019, new Vector3(84.1f, 7.3f, 94.005f), Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)));
        return wo;
    }

    public static Session MakeSession()
    {
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        s.GameEventSequence = 1;
        return s;
    }
}
public class StubWO : ACE.Server.WorldObjects.WorldObject { }
