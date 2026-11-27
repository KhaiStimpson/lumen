// _CustomerNotes partial: add, pin and refresh notes without leaving the customer page.
window.Shop = window.Shop || {};

(function ($, Shop) {
    'use strict';

    if (Shop.CustomerNotes) {
        return;
    }

    function headers() {
        return { RequestVerificationToken: Shop.antiforgeryToken() };
    }

    function refresh() {
        var $panel = $('#customer-notes');
        $.get($panel.data('url'))
            .done(function (markup) {
                $panel.html(markup);
            })
            .fail(function (xhr) {
                Shop.toast('Could not refresh notes (' + xhr.status + ')', 'error');
            });
    }

    function prependNote(note) {
        var $list = $('#customer-notes-list');
        $list.find('.text-muted').first().remove();
        $list.html('<li class="list-group-item customer-note" data-note-id="' + note.id + '"><div class="note-body">' + note.body + '</div><div class="small text-muted">' + note.author + '</div></li>' + $list.html());
    }

    $(document).on('submit', '#customer-note-form', function (e) {
        e.preventDefault();
        var $body = $('#customer-note-body');
        var body = $.trim($body.val());
        if (!body) {
            return;
        }

        $.ajax({
            type: 'POST',
            url: '?handler=AddNote',
            data: { body: body },
            headers: headers()
        })
            .done(function (note) {
                prependNote(note);
                $body.val('');
            })
            .fail(function (xhr) {
                var message = (xhr.responseJSON && xhr.responseJSON.message) || 'Could not add the note';
                Shop.toast(message, 'error');
            });
    });

    $(document).on('click', '#customer-notes .note-pin', function () {
        var $button = $(this);
        var noteId = $button.closest('.customer-note').data('note-id');
        var pinned = $button.data('pinned') !== true;

        $.ajax({
            type: 'POST',
            url: '?handler=PinNote',
            data: { noteId: noteId, pinned: pinned },
            headers: headers()
        })
            .done(refresh)
            .fail(function (xhr) {
                Shop.toast('Could not update the note (' + xhr.status + ')', 'error');
            });
    });

    Shop.CustomerNotes = { refresh: refresh };
})(jQuery, window.Shop);
