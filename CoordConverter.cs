using UnityEngine;

namespace OSMImporter
{
    public static class CoordConverter
    {
        public static double CenterLat = 0;
        public static double CenterLon = 0;
        public static bool IsInitialized = false;

        public const double MetersPerDegLat = 111320.0;

        public static void InitializeFromBounds(double minLat, double maxLat, double minLon, double maxLon)
        {
            CenterLat = (minLat + maxLat) / 2.0;
            CenterLon = (minLon + maxLon) / 2.0;
            IsInitialized = true;

            Plugin.Log.LogInfo($"Map center calculated: Lat={CenterLat}, Lon={CenterLon}");
        }

        public static Vector3 LatLonToGame(double lat, double lon)
        {
            double metersPerDegLon = 111320.0 * System.Math.Cos(CenterLat * System.Math.PI / 180.0);

            float x = (float)((lon - CenterLon) * metersPerDegLon);
            float z = (float)((lat - CenterLat) * MetersPerDegLat);

            return new Vector3(x, 0f, z);
        }
    }
}