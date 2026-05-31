namespace DoseConverter.ViewModels
{
    /// <summary>
    /// Represents a single Eclipse rigid (frame-of-reference) registration that can be
    /// used to initialise the diffeomorphic demons deformable registration.
    /// </summary>
    public class RigidRegistrationOption
    {
        /// <summary>Sentinel label shown when no rigid pre-alignment is desired.</summary>
        public const string NoneSentinel = "(none)";

        /// <summary>Display string shown in the ComboBox.</summary>
        public string DisplayString { get; }

        /// <summary>
        /// Row-major, flattened 4×4 homogeneous transform matrix (mm) mapping the source
        /// (moving) image frame of reference to the target (fixed) image frame of reference.
        /// Null for the sentinel "none" entry.
        /// </summary>
        public double[] MatrixRowMajor { get; }

        /// <summary>Creates the sentinel "none" option.</summary>
        public RigidRegistrationOption()
        {
            DisplayString  = NoneSentinel;
            MatrixRowMajor = null;
        }

        /// <summary>Creates a named option backed by an ESAPI registration matrix.</summary>
        public RigidRegistrationOption(string displayString, double[] matrixRowMajor)
        {
            DisplayString  = displayString;
            MatrixRowMajor = matrixRowMajor;
        }

        public override string ToString() => DisplayString;
    }
}
