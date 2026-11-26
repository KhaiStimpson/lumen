// Orders/Edit: saves the order over AJAX and refreshes the total.
(function ($) {
    'use strict';

    var $form = $('#order-edit-form');

    $form.on('submit', function (e) {
        e.preventDefault();
        if (!$form.valid()) {
            return;
        }

        $.post('?handler=Save', $form.serialize())
            .done(function (result) {
                $('#order-total').text(result.total);
                Shop.toast('Order saved');
            })
            .fail(function (xhr) {
                Shop.toast('Could not save the order (' + xhr.status + ')', 'error');
            });
    });
})(jQuery);
