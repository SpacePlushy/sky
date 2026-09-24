using System.Globalization;
using SGP4Methods;
using Sky.Orbital.Elements;

namespace Sky.Orbital.Propagation;

/// <summary>
/// Propagates one element set with Vallado's reference SGP4 (see src/Sky.Sgp4/NOTICE.md),
/// using WGS-72 constants as the element sets require.
/// </summary>
/// <remarks>
/// Not thread-safe: SGP4 keeps integrator state between calls for deep-space orbits.
/// Create one propagator per thread.
/// </remarks>
public sealed class Sgp4Propagator
{
    // SGP4's epoch argument is days since 1949 December 31 00:00 UT (Julian date 2433281.5).
    private static readonly DateTimeOffset Sgp4EpochOrigin = new(1949, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private const double Sgp4EpochJulianDate = 2433281.5;

    // Unit conversions written exactly as Vallado's TLE reader writes them, so the
    // floating-point results match the reference output bit for bit where possible.
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RevPerDayPerRadPerMinute = 1440.0 / (2.0 * Math.PI); // "xpdotp" upstream

    private readonly SGP4Lib _sgp4 = new();
    private readonly Sgp4Error _initializationError;
    private SGP4Lib.elsetrec _record = new();

    private Sgp4Propagator(MeanElements elements, OperationMode mode)
    {
        Elements = elements;
        Mode = mode;

        _sgp4.sgp4init(
            SGP4Lib.gravconsttype.wgs72,
            mode == OperationMode.Afspc ? 'a' : 'i',
            elements.CatalogNumber.ToString(CultureInfo.InvariantCulture),
            Sgp4EpochDays(elements.Epoch),
            elements.BStar,
            elements.MeanMotionDot / (RevPerDayPerRadPerMinute * 1440.0),
            elements.MeanMotionDdot / (RevPerDayPerRadPerMinute * 1440.0 * 1440),
            elements.Eccentricity,
            elements.ArgumentOfPericenter * DegreesToRadians,
            elements.Inclination * DegreesToRadians,
            elements.MeanAnomaly * DegreesToRadians,
            elements.MeanMotion / RevPerDayPerRadPerMinute,
            elements.RightAscensionOfAscendingNode * DegreesToRadians,
            ref _record);

        // sgp4init finishes by propagating to t = 0. An error there means the
        // element set cannot be propagated at all.
        _initializationError = (Sgp4Error)_record.error;
    }

    /// <summary>The element set this propagator was built from.</summary>
    public MeanElements Elements { get; }

    /// <summary>The SGP4 operation mode in use.</summary>
    public OperationMode Mode { get; }

    /// <summary>Creates a propagator for the given elements.</summary>
    public static Sgp4Propagator Create(MeanElements elements, OperationMode mode = OperationMode.Improved)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return new Sgp4Propagator(elements, mode);
    }

    /// <summary>Propagates to a time given in minutes since the element epoch.</summary>
    public PropagationResult Propagate(double minutesSinceEpoch)
    {
        if (_initializationError != Sgp4Error.None)
        {
            return new PropagationResult(_initializationError);
        }

        double[] r = new double[3];
        double[] v = new double[3];
        _sgp4.sgp4(ref _record, minutesSinceEpoch, r, v);

        return _record.error == 0
            ? new PropagationResult(new TemeState(new Vec3(r[0], r[1], r[2]), new Vec3(v[0], v[1], v[2])))
            : new PropagationResult((Sgp4Error)_record.error);
    }

    /// <summary>Propagates to an instant.</summary>
    public PropagationResult Propagate(DateTimeOffset instant) =>
        Propagate((instant - Elements.Epoch).Ticks / (double)TimeSpan.TicksPerMinute);

    /// <summary>
    /// The epoch as SGP4 expects it: days since 1949 December 31 00:00 UT, computed the way
    /// Vallado's reference code computes it.
    /// </summary>
    /// <remarks>
    /// Upstream forms (Julian date of midnight + fraction of day) - 2433281.5. The sum rounds
    /// at Julian-date magnitude, which shifts the epoch by up to about 20 microseconds. Only the
    /// deep-space lunar and solar terms see the epoch, but for very eccentric orbits that shift
    /// moves the result by millimeters. Reproducing the arithmetic keeps Sky within 0.2 mm of
    /// Vallado's verification output, python-sgp4, and Skyfield.
    /// </remarks>
    private static double Sgp4EpochDays(DateTimeOffset epoch)
    {
        DateTime utc = epoch.UtcDateTime;
        DateTime midnight = utc.Date;
        double julianDateOfMidnight = Sgp4EpochJulianDate + (midnight - Sgp4EpochOrigin.UtcDateTime).Days;
        double fractionOfDay = (utc - midnight).Ticks / (double)TimeSpan.TicksPerDay;
        return (julianDateOfMidnight + fractionOfDay) - Sgp4EpochJulianDate;
    }
}
