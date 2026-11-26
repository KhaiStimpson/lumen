// <currency-input> tag helper: normalises the typed amount to two decimal places on blur.
(function ($) {
    'use strict';

    $(document).on('blur', 'input.currency-input', function () {
        var $input = $(this);
        var raw = String($input.val()).replace(/[^0-9.\-]/g, '');
        var value = parseFloat(raw);
        $input.val(isNaN(value) ? '' : value.toFixed(2));
    });
})(jQuery);
