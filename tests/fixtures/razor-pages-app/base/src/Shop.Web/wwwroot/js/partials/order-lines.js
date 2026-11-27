// _OrderLines partial: fills in each line total from the row's data attributes.
(function ($) {
    'use strict';

    function lineTotal($row) {
        var unitPrice = parseFloat($row.data('unit-price')) || 0;
        var discount = parseFloat($row.data('discount')) || 0;
        var qty = parseInt($row.find('.line-qty').text(), 10) || 0;
        return Math.max(0, unitPrice * qty - discount);
    }

    $(function () {
        $('#order-lines .order-line').each(function () {
            var $row = $(this);
            $row.find('.line-total').text(Shop.formatMoney(lineTotal($row)));
        });
    });
})(jQuery);
