using LaunchMonitor.Proto;
using gspro_r10.OpenConnect;
using System;
using System.Text;

namespace gspro_r10.bluetooth
{
  public static class LaunchMonitorMetricsHelper
  {
    private static readonly double METERS_PER_S_TO_MILES_PER_HOUR = 2.2369;
    private static readonly float FEET_TO_METERS = 1 / 3.281f;

    public static BallData? BallDataFromLaunchMonitorMetrics(BallMetrics? ballMetrics)
    {
      if (ballMetrics == null) return null;
      return new BallData()
      {
        HLA = ballMetrics.LaunchDirection,
        VLA = ballMetrics.LaunchAngle,
        Speed = ballMetrics.BallSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        SpinAxis = ballMetrics.SpinAxis * -1,
        TotalSpin = ballMetrics.TotalSpin,
        SideSpin = ballMetrics.TotalSpin * Math.Sin(-1 * ballMetrics.SpinAxis * Math.PI / 180),
        BackSpin = ballMetrics.TotalSpin * Math.Cos(-1 * ballMetrics.SpinAxis * Math.PI / 180)
      };
    }

    public static ClubData? ClubDataFromLaunchMonitorMetrics(ClubMetrics? clubMetrics)
    {
      if (clubMetrics == null) return null;
      return new ClubData()
      {
        Speed = clubMetrics.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        SpeedAtImpact = clubMetrics.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        AngleOfAttack = clubMetrics.AttackAngle,
        FaceToTarget = clubMetrics.ClubAngleFace,
        Path = clubMetrics.ClubAnglePath
      };
    }

    public static void LogMetrics(Metrics? metrics)
    {
      if (metrics == null)
        return;
      try
      {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"===== Shot {metrics.ShotId} =====");
        sb.AppendLine($"{"Ball Metrics",-40}│ {"Club Metrics",-40}│ {"Swing Metrics",-40}");
        sb.AppendLine($"{new string('─', 40)}┼─{new string('─', 40)}┼─{new string('─', 40)}");
        sb.Append($" {"BallSpeed:",-15} {metrics.BallMetrics?.BallSpeed * METERS_PER_S_TO_MILES_PER_HOUR,-22} │");
        sb.Append($" {"Club Speed:",-20} {metrics.ClubMetrics?.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,-18} │");
        sb.AppendLine($" {"Backswing Start:",-20} {metrics.SwingMetrics?.BackSwingStartTime,-17}");

        sb.Append($" {"VLA:",-15} {metrics.BallMetrics?.LaunchAngle,-22} │");
        sb.Append($" {"Club Path:",-20} {metrics.ClubMetrics?.ClubAnglePath,-18} │");
        sb.AppendLine($" {"Downswing Start:",-20} {metrics.SwingMetrics?.DownSwingStartTime,-17}");

        sb.Append($" {"HLA:",-15} {metrics.BallMetrics?.LaunchDirection,-22} │");
        sb.Append($" {"Club Face:",-20} {metrics.ClubMetrics?.ClubAngleFace,-18} │");
        sb.AppendLine($" {"Impact time:",-20} {metrics.SwingMetrics?.ImpactTime,-17}");

        uint? backswingDuration = metrics.SwingMetrics?.DownSwingStartTime - metrics.SwingMetrics?.BackSwingStartTime;
        sb.Append($" {"Spin Axis:",-15} {metrics.BallMetrics?.SpinAxis * -1,-22} │");
        sb.Append($" {"Attack Angle:",-20} {metrics.ClubMetrics?.AttackAngle,-18} │");
        sb.AppendLine($" {"Backswing duration:",-20} {backswingDuration,-17}");

        uint? downswingDuration = metrics.SwingMetrics?.ImpactTime - metrics.SwingMetrics?.DownSwingStartTime;
        sb.Append($" {"Total Spin:",-15} {metrics.BallMetrics?.TotalSpin,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Downswing duration:",-20} {downswingDuration,-17}");

        sb.Append($" {"Ball Type:",-15} {metrics.BallMetrics?.GolfBallType,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Tempo:",-20} {(float)(backswingDuration ?? 0) / downswingDuration,-17}");

        sb.Append($" {"Spin Calc:",-15} {metrics.BallMetrics?.SpinCalculationType,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Normal/Practice:",-20} {metrics.ShotType,-17}");
        BluetoothLogger.Info(sb.ToString());
      }
      catch (Exception e)
      {
        Console.WriteLine(e);
      }
    }

    public static float CalculateTeeRange(float teeDistanceFeet) => teeDistanceFeet * FEET_TO_METERS;
  }
}
