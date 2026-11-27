// Orders/Edit: saves the order over AJAX, refreshes the total and reloads the lines on demand.
(function ($) {
    'use strict';

    var $form = $('#order-edit-form');

    function showErrors(xhr) {
        var body = xhr.responseJSON || {};
        Shop.toast(body.message || 'Could not save the order (' + xhr.status + ')', 'error');
    }

    $form.on('submit', function (e) {
        e.preventDefault();
        if (!$form.valid()) {
            return;
        }

        $.post('?handler=Save', $form.serialize())
            .done(function (result) {
                $('#order-total').text(result.data.total);
                Shop.toast(result.message || 'Order saved');
            })
            .fail(showErrors);
    });

    $('#refresh-lines').on('click', function () {
        var $button = $(this).prop('disabled', true);

        $.getJSON('?handler=Lines')
            .done(function (result) {
                Shop.OrderLines.render(result.data.lines);
                $('#order-total').text(Shop.formatMoney(result.data.total));
            })
            .fail(function (xhr) {
                Shop.toast('Could not reload the lines (' + xhr.status + ')', 'error');
            })
            .always(function () {
                $button.prop('disabled', false);
            });
    });
})(jQuery);
