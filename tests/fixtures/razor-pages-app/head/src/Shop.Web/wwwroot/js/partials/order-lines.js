// _OrderLines partial: fills in each line total from the row's data attributes,
// and lets the hosting page re-render the rows from JSON.
window.Shop = window.Shop || {};

(function ($, Shop) {
    'use strict';

    function lineTotal($row) {
        var unitPrice = parseFloat($row.data('unit-price')) || 0;
        var discount = parseFloat($row.data('discount')) || 0;
        var qty = parseInt($row.find('.line-qty').text(), 10) || 0;
        return Math.max(0, unitPrice * qty - discount);
    }

    function recalculate() {
        var sum = 0;
        $('#order-lines .order-line').each(function () {
            var $row = $(this);
            var total = lineTotal($row);
            sum += total;
            $row.find('.line-total').text(Shop.formatMoney(total));
        });
        $('#order-lines-total').text(Shop.formatMoney(sum));
    }

    function render(lines) {
        var $body = $('#order-lines tbody').empty();
        $.each(lines, function (_, line) {
            var $row = $('<tr class="order-line"></tr>')
                .attr('data-line-id', line.id)
                .attr('data-unit-price', line.unitPrice)
                .attr('data-discount', line.discount);
            $('<td class="line-product"></td>').text(line.product || '').appendTo($row);
            $('<td class="text-end line-qty"></td>').text(line.quantity).appendTo($row);
            $('<td class="text-end line-unit-price"></td>').text(Shop.formatMoney(line.unitPrice)).appendTo($row);
            $('<td class="text-end line-discount"></td>').text(Shop.formatMoney(line.discount)).appendTo($row);
            $('<td class="text-end line-total"></td>').appendTo($row);
            $body.append($row);
        });
        recalculate();
    }

    Shop.OrderLines = { render: render, recalculate: recalculate };

    $(recalculate);
})(jQuery, window.Shop);
