// Admin/Dashboard: refreshes the stat cards and loads the top products and per-status counts.
window.Shop = window.Shop || {};

(function ($, Shop) {
    'use strict';

    var ordersSection = 'BulkEdit';

    function renderStats(stats) {
        $('[data-stat="orderCount"]').text(stats.orderCount);
        $('[data-stat="revenue"]').text(Shop.formatMoney(stats.revenue));
        $('[data-stat="averageOrderValue"]').text(Shop.formatMoney(stats.averageOrderValue));
        $('[data-stat="unpaidInvoiceTotal"]').text(Shop.formatMoney(stats.unpaidInvoiceTotal));
        if (Shop.DashboardCharts) {
            Shop.DashboardCharts.drawStatus(stats.ordersByStatus || {});
        }
    }

    function loadStats() {
        return $.getJSON('?handler=Stats')
            .done(function (result) {
                renderStats(result.data);
            })
            .fail(function (xhr) {
                Shop.toast('Could not load dashboard stats (' + xhr.status + ')', 'error');
            });
    }

    function loadTopProducts() {
        return $.getJSON('?handler=TopProducts', { status: 'Paid' })
            .done(function (result) {
                var $list = $('#dashboard-top-products').empty();
                $.each(result.data, function (_, product) {
                    $('<li></li>')
                        .text(product.name + ' — ' + product.quantity + ' sold')
                        .appendTo($list);
                });
            })
            .fail(function (xhr) {
                Shop.toast('Could not load top products (' + xhr.status + ')', 'error');
            });
    }

    function loadOrderSummary() {
        return $.getJSON('/Orders/' + ordersSection + '?handler=Summary')
            .done(function (result) {
                $('#dashboard-cards').attr('data-status-summary', JSON.stringify(result.data));
            })
            .fail(function (xhr) {
                Shop.toast('Could not load the order summary (' + xhr.status + ')', 'error');
            });
    }

    $('#dashboard-refresh').on('click', Shop.debounce(function () {
        loadStats();
        loadTopProducts();
        loadOrderSummary();
    }, 300));

    $(function () {
        loadStats();
        loadTopProducts();
        loadOrderSummary();
    });
})(jQuery, window.Shop);
