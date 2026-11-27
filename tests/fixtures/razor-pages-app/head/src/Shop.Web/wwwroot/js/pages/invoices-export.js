// Invoices/Export: previews the first rows of the export before downloading the CSV.
(function ($) {
    'use strict';

    var $form = $('#invoice-export-form');
    var $result = $('#invoice-export-result');
    var $rows = $('#invoice-export-rows');

    function cell(text, alignEnd) {
        var $td = $('<td></td>').text(text);
        if (alignEnd) {
            $td.addClass('text-end');
        }
        return $td;
    }

    function renderRows(rows) {
        $rows.empty();
        $.each(rows, function (_, row) {
            $('<tr></tr>')
                .append(cell(row.number))
                .append(cell(row.orderNumber))
                .append(cell(row.customerName))
                .append(cell(Shop.formatDate(row.issuedOn)))
                .append(cell(Shop.formatDate(row.dueOn)))
                .append(cell(Shop.formatMoney(row.amount), true))
                .append(cell(row.daysOverdue, true))
                .appendTo($rows);
        });
        $('#invoice-export-count').text(rows.length + ' rows');
        $result.removeClass('d-none');
    }

    $('#invoice-export-preview').on('click', function () {
        if (!$form.valid()) {
            return;
        }

        $.post('?handler=Preview', $form.serialize())
            .done(function (result) {
                renderRows(result.data);
            })
            .fail(function (xhr) {
                var body = xhr.responseJSON || {};
                Shop.toast(body.message || 'Preview failed (' + xhr.status + ')', 'error');
            });
    });
})(jQuery);
