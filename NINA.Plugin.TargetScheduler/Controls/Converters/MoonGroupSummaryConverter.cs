using NINA.Plugin.TargetScheduler.Planning;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace NINA.Plugin.TargetScheduler.Controls.Converters {

    /// <summary>
    /// Summarizes a moon avoidance group (project or target) for its collapsed dropdown header, so the
    /// interesting rows can be found without expanding every group.
    ///
    /// Works at either grouping level: a project group's items are target subgroups, a target group's items
    /// are the exposure plan rows, so the rows are gathered recursively.
    /// </summary>
    public class MoonGroupSummaryConverter : IValueConverter {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            CollectionViewGroup group = value as CollectionViewGroup;
            if (group == null) { return string.Empty; }

            List<MoonAvoidanceAnalysisRow> rows = GroupRows(group);
            if (rows.Count == 0) { return string.Empty; }

            if (rows.All(r => r.NeverImagable)) { return "not imagable tonight"; }

            int blocked = rows.Count(r => r.IsBlockedAllNight);
            string plans = rows.Count == 1 ? "plan" : "plans";

            if (blocked == 0) {
                return $"{rows.Count} {plans}, all have time tonight";
            }

            return blocked == rows.Count
                ? $"{rows.Count} {plans}, all blocked all night"
                : $"{rows.Count} {plans}, {blocked} blocked all night";
        }

        /// <summary>
        /// All exposure plan rows under a group, however deeply nested.
        /// </summary>
        internal static List<MoonAvoidanceAnalysisRow> GroupRows(CollectionViewGroup group) {
            List<MoonAvoidanceAnalysisRow> rows = new List<MoonAvoidanceAnalysisRow>();
            Collect(group, rows);
            return rows;
        }

        private static void Collect(CollectionViewGroup group, List<MoonAvoidanceAnalysisRow> rows) {
            foreach (object item in group.Items) {
                if (item is MoonAvoidanceAnalysisRow row) {
                    rows.Add(row);
                } else if (item is CollectionViewGroup subgroup) {
                    Collect(subgroup, rows);
                }
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Overall status of a moon avoidance group, so a collapsed dropdown header can be colored without
    /// expanding it: "Clear" (everything has time), "Blocked" (nothing does), "Mixed", or "NotImagable".
    /// </summary>
    public class MoonGroupStatusConverter : IValueConverter {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            CollectionViewGroup group = value as CollectionViewGroup;
            if (group == null) { return "None"; }

            List<MoonAvoidanceAnalysisRow> rows = MoonGroupSummaryConverter.GroupRows(group);
            if (rows.Count == 0) { return "None"; }
            if (rows.All(r => r.NeverImagable)) { return "NotImagable"; }

            List<MoonAvoidanceAnalysisRow> imagable = rows.Where(r => !r.NeverImagable).ToList();
            if (imagable.All(r => r.HasTimeTonight)) { return "Clear"; }
            if (imagable.All(r => r.IsBlockedAllNight)) { return "Blocked"; }
            return "Mixed";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
