using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChinookWorker;

public static class CheckerBoard
{
    public const int CB_FREE = 0;
    public const int CB_WHITE = 1;
    public const int CB_BLACK = 2;
    public const int CB_MAN = 4;
    public const int CB_KING = 8;

    public const int PIECE_WHITE_MAN = CB_WHITE | CB_MAN;  // 5
    public const int PIECE_BLACK_MAN = CB_BLACK | CB_MAN;  // 6
    public const int PIECE_WHITE_KING = CB_WHITE | CB_KING; // 9
    public const int PIECE_BLACK_KING = CB_BLACK | CB_KING; // 10

    [StructLayout(LayoutKind.Sequential)]
    public struct Coor
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CBmove
    {
        public int jumps;
        public int newpiece;
        public int oldpiece;
        public Coor from;
        public Coor to;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public Coor[] path;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public Coor[] del;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public int[] delpiece;

        public static CBmove Create() => new CBmove
        {
            path = new Coor[12],
            del = new Coor[12],
            delpiece = new int[12]
        };
    }

    public const string DllName = "Kingsrow64.dll";

    
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int getmove(
        [In, Out] int[,] b,
        int color,
        double maxtime,
        [MarshalAs(UnmanagedType.LPStr)] StringBuilder str,
        ref int playnow,
        int info,
        int unused,
        ref CBmove move);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int enginecommand(
        [MarshalAs(UnmanagedType.LPStr)] string command,
        [MarshalAs(UnmanagedType.LPStr)] StringBuilder reply);
}