namespace Streifen.Windows.Core;

public enum AppSize
{
    XS,
    S,
    M,
    L,
    XL,
    Full
}

public static class AppSizeExtensions
{
    /// <summary>
    /// Slice count per AppSize per ScreenClass.
    /// Replaces the old float ratio system with integer grid slices.
    /// </summary>
    public static int Slices(this AppSize size, ScreenClass screen) => (size, screen) switch
    {
        (AppSize.XS, ScreenClass.Laptop) => 1,
        (AppSize.XS, ScreenClass.Desktop) => 1,
        (AppSize.XS, ScreenClass.Ultrawide) => 1,

        (AppSize.S, ScreenClass.Laptop) => 2,
        (AppSize.S, ScreenClass.Desktop) => 2,
        (AppSize.S, ScreenClass.Ultrawide) => 2,

        (AppSize.M, ScreenClass.Laptop) => 4,
        (AppSize.M, ScreenClass.Desktop) => 2,
        (AppSize.M, ScreenClass.Ultrawide) => 3,

        (AppSize.L, ScreenClass.Laptop) => 4,
        (AppSize.L, ScreenClass.Desktop) => 3,
        (AppSize.L, ScreenClass.Ultrawide) => 4,

        (AppSize.XL, ScreenClass.Laptop) => 4,
        (AppSize.XL, ScreenClass.Desktop) => 4,
        (AppSize.XL, ScreenClass.Ultrawide) => 6,

        (AppSize.Full, ScreenClass.Laptop) => 4,
        (AppSize.Full, ScreenClass.Desktop) => 6,
        (AppSize.Full, ScreenClass.Ultrawide) => 8,

        _ => 3
    };
}
