// CartSummary view component: pulses the badge when the count changes.
(function ($) {
    'use strict';

    $(document).on('shop:cart-changed', function (e, cart) {
        var $summary = $('#cart-summary');
        $summary.find('.cart-count').text(cart.count);
        $summary.find('.cart-subtotal').text(Shop.formatMoney(cart.subtotal));
        $summary.addClass('pulse');
        window.setTimeout(function () { $summary.removeClass('pulse'); }, 600);
    });
})(jQuery);
