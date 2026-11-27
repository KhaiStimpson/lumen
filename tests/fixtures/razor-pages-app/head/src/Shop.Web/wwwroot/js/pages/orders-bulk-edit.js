// Orders/BulkEdit: multi-select rows and apply a status change to all of them at once.
(function ($) {
    'use strict';

    var $table = $('#bulk-edit-table');
    var $apply = $('#bulk-apply');

    $table.bulkSelect({
        checkbox: '.bulk-select',
        selectAll: '#bulk-select-all',
        rowClass: 'table-active',
        onChange: function (selected) {
            $apply.prop('disabled', selected.length === 0);
            $apply.text(selected.length ? 'Apply to ' + selected.length + ' selected' : 'Apply to selected');
        }
    });

    function loadSummary() {
        $.getJSON('?handler=Summary')
            .done(function (result) {
                var parts = $.map(result.data, function (s) {
                    return s.status + ': ' + s.count;
                });
                $('#bulk-summary').text(parts.join(' · '));
            })
            .fail(function () {});
    }

    $apply.on('click', function () {
        var ids = $table.bulkSelect('selected');
        if (!ids.length || !window.confirm('Update ' + ids.length + ' orders?')) {
            return;
        }

        $apply.prop('disabled', true);

        $.post('?handler=Apply', {
            'Input.OrderIds': ids,
            'Input.NewStatus': $('#bulk-new-status').val(),
            'Input.DiscountPercent': $('#bulk-discount').val()
        })
            .done(function (result) {
                Shop.toast(result.message);
                window.location.reload();
            })
            .fail(function (xhr) {
                var body = xhr.responseJSON || {};
                Shop.toast(body.message || 'Bulk update failed', 'error');
                $apply.prop('disabled', false);
            });
    });

    loadSummary();
})(jQuery);
