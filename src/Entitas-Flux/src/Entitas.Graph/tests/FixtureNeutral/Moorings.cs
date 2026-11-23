namespace Harbor
{
    /// <summary>
    /// Holds a moored craft with grapples, each told which gantry it belongs to. The craft is a
    /// field set by Hold: a grapple reaches it through its gantry, so which entity a grapple's
    /// write lands on is no parameter anybody passes.
    /// </summary>
    public sealed class Gantry
    {
        readonly IGrapple[] _grapples;
        PortEntity _craft;

        public Gantry(params IGrapple[] grapples) => _grapples = grapples;

        public PortEntity Craft => _craft;

        public void Hold(PortEntity craft)
        {
            _craft = craft;
            foreach (IGrapple grapple in _grapples)
            {
                if (grapple.Gantry == this)
                    grapple.Latch();
            }
        }

        public void LetGo()
        {
            foreach (IGrapple grapple in _grapples)
            {
                if (grapple.Gantry == this)
                    grapple.Unlatch();
            }
            _craft = null;
        }
    }

    // Grapples are reached through an interface an abstract class implements: the magnetic one
    // latches and unlatches by an override two steps from the call.
    public interface IGrapple
    {
        Gantry Gantry { get; }
        void Latch();
        void Unlatch();
    }

    public abstract class Grapple : IGrapple
    {
        public Gantry Owner;

        public Gantry Gantry => Owner;

        public PortEntity Gripped => Owner != null ? Owner.Craft : null;

        public abstract void Latch();
        public abstract void Unlatch();
    }

    public sealed class MagGrapple : Grapple
    {
        public override void Latch() => Gripped.AddLodestone(1);

        public override void Unlatch() => Gripped.RemoveLodestone();
    }

    public static class Moorings
    {
        /// <summary>Lets the gantry go, darkens the slipway the craft leaves and casts the hawser off.</summary>
        public static void Unmoor(this PortEntity craft, PortEntity slipway)
        {
            craft.gantryLink.Value.LetGo();
            slipway.RemoveFloodlight();
            craft.RemoveHawser();
        }

        /// <summary>Pays out a hawser; one pulled too far snaps the spool off instead.</summary>
        public static void Slacken(this PortEntity craft)
        {
            if (craft.hawser.Value > 9)
            {
                craft.RemoveSpool();
                return;
            }

            craft.ReplaceHawser(craft.hawser.Value + 1);
        }
    }

    /// <summary>The mooring master's panel: a craft is moored with its hawser, spool and light, then held; a slipway is lit.</summary>
    public sealed class MooringPanel
    {
        public void OnMoorPressed(Gantry gantry)
        {
            PortEntity craft = Make.Blank();
            craft.AddHawser(0);
            craft.AddSpool(1);
            craft.AddFloodlight(1);
            craft.AddGantryLink(gantry);
            gantry.Hold(craft);
        }

        public void OnSlipwayBuilt(int number) => Make.Blank().AddSlipway(number).AddFloodlight(5);
    }
}
