// Polyfill para System.Runtime.CompilerServices.IsExternalInit
// (necessário para 'record' e 'init' setters no .NET Core 3.1)
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif


#if !NET6_0_OR_GREATER
// Polyfill para DateOnly (introduzido no .NET 6)
namespace System
{
    public readonly struct DateOnly : IEquatable<DateOnly>, IComparable<DateOnly>
    {
        private readonly int _dayNumber;

        public DateOnly(int year, int month, int day) =>
            _dayNumber = (int)(new DateTime(year, month, day) - new DateTime(1, 1, 1)).TotalDays;

        private DateOnly(int dayNumber) => _dayNumber = dayNumber;

        public static DateOnly FromDateTime(DateTime dt) =>
            new DateOnly((int)(dt.Date - new DateTime(1, 1, 1)).TotalDays);

        public static DateOnly Parse(string s) => FromDateTime(DateTime.Parse(s));

        public static DateOnly ParseExact(string s, string format) =>
            FromDateTime(DateTime.ParseExact(s, format, System.Globalization.CultureInfo.InvariantCulture));

        public static bool TryParseExact(string s, string format, out DateOnly result)
        {
            if (DateTime.TryParseExact(s, format, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
            {
                result = FromDateTime(dt);
                return true;
            }
            result = default;
            return false;
        }

        public DateTime ToDateTime(TimeOnly time) => ToDateTime().Add(time.ToTimeSpan());
        public DateTime ToDateTime() => new DateTime(1, 1, 1).AddDays(_dayNumber);

        public int Year  => ToDateTime().Year;
        public int Month => ToDateTime().Month;
        public int Day   => ToDateTime().Day;

        public bool Equals(DateOnly other) => _dayNumber == other._dayNumber;
        public int CompareTo(DateOnly other) => _dayNumber.CompareTo(other._dayNumber);
        public override bool Equals(object obj) => obj is DateOnly d && Equals(d);
        public override int GetHashCode() => _dayNumber;
        public override string ToString() => ToDateTime().ToString("yyyy-MM-dd");
        public string ToString(string format) => ToDateTime().ToString(format);

        public static bool operator ==(DateOnly a, DateOnly b) => a._dayNumber == b._dayNumber;
        public static bool operator !=(DateOnly a, DateOnly b) => a._dayNumber != b._dayNumber;
        public static bool operator  <(DateOnly a, DateOnly b) => a._dayNumber  < b._dayNumber;
        public static bool operator  >(DateOnly a, DateOnly b) => a._dayNumber  > b._dayNumber;
        public static bool operator <=(DateOnly a, DateOnly b) => a._dayNumber <= b._dayNumber;
        public static bool operator >=(DateOnly a, DateOnly b) => a._dayNumber >= b._dayNumber;
    }

    // Stub mínimo para TimeOnly (usado pelo polyfill DateOnly.ToDateTime(TimeOnly))
    public readonly struct TimeOnly
    {
        private readonly long _ticks;
        public TimeOnly(int hour, int minute) => _ticks = new TimeSpan(hour, minute, 0).Ticks;
        internal TimeSpan ToTimeSpan() => new TimeSpan(_ticks);
    }
}
#endif
