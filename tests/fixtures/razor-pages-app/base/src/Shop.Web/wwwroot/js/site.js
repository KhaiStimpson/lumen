// Site-wide helpers, loaded by _Layout on every page.
window.Shop = window.Shop || {};

(function ($, Shop) {
    'use strict';

    Shop.toast = function (message, level) {
        var $toast = $('<div class="toast align-items-center border-0" role="status" aria-live="polite"></div>');
        $toast.addClass(level === 'error' ? 'text-bg-danger' : 'text-bg-success');
        $('<div class="toast-body"></div>').text(message).appendTo($toast);
        $('#toast-container').append($toast);
        bootstrap.Toast.getOrCreateInstance($toast[0], { delay: 4000 }).show();
    };

    Shop.formatMoney = function (value) {
        return new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' }).format(value);
    };

    $(function () {
        $('[data-confirm]').on('click', function (e) {
            if (!window.confirm($(this).data('confirm'))) {
                e.preventDefault();
            }
        });
    });
})(jQuery, window.Shop);
