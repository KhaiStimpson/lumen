// Admin/Dashboard: a dependency-free bar chart of orders by status, drawn on a canvas.
window.Shop = window.Shop || {};

(function ($, Shop) {
    'use strict';

    var colours = ['#0d6efd', '#198754', '#ffc107', '#6f42c1', '#dc3545'];

    function drawStatus(counts) {
        var canvas = document.getElementById('dashboard-status-chart');
        if (!canvas || !canvas.getContext) {
            return;
        }

        var ctx = canvas.getContext('2d');
        var labels = Object.keys(counts);
        var max = Math.max.apply(null, labels.map(function (k) { return counts[k]; }).concat([1]));
        var width = canvas.width = canvas.clientWidth || 480;
        var height = canvas.height;
        var barWidth = labels.length ? Math.floor((width - 20) / labels.length) - 10 : 0;

        ctx.clearRect(0, 0, width, height);
        ctx.font = '12px system-ui, sans-serif';
        ctx.textAlign = 'center';

        labels.forEach(function (label, i) {
            var value = counts[label];
            var barHeight = Math.round((height - 40) * value / max);
            var x = 10 + i * (barWidth + 10);
            var y = height - 20 - barHeight;

            ctx.fillStyle = colours[i % colours.length];
            ctx.fillRect(x, y, barWidth, barHeight);
            ctx.fillStyle = '#212529';
            ctx.fillText(String(value), x + barWidth / 2, y - 4);
            ctx.fillText(label, x + barWidth / 2, height - 6);
        });
    }

    Shop.DashboardCharts = { drawStatus: drawStatus };

    $(function () {
        var initial = {};
        $('#dashboard-cards').closest('main').find('.list-inline-item a').each(function () {
            var match = /^(.+) \((\d+)\)$/.exec($.trim($(this).text()));
            if (match) {
                initial[match[1]] = parseInt(match[2], 10);
            }
        });
        drawStatus(initial);
    });
})(jQuery, window.Shop);
