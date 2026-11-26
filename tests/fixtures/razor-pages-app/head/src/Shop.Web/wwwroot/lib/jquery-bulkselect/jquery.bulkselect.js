/*!
 * jQuery BulkSelect v2.1.0
 * Shift-click range selection and select-all for checkbox tables.
 * Released under the MIT licence.
 */
(function (factory) {
    'use strict';
    if (typeof define === 'function' && define.amd) {
        define(['jquery'], factory);
    } else if (typeof module === 'object' && module.exports) {
        module.exports = factory(require('jquery'));
    } else {
        factory(window.jQuery);
    }
}(function ($) {
    'use strict';

    var pluginName = 'bulkSelect';
    var dataKey = 'plugin_' + pluginName;

    var defaults = {
        checkbox: 'input[type="checkbox"]',
        selectAll: null,
        rowClass: 'selected',
        valueAttr: 'value',
        onChange: null
    };

    function BulkSelect(element, options) {
        this.$element = $(element);
        this.settings = $.extend({}, defaults, options);
        this.lastIndex = null;
        this.init();
    }

    BulkSelect.prototype.init = function () {
        var self = this;

        this.$element.on('click.' + pluginName, this.settings.checkbox, function (e) {
            self.handleClick(this, e.shiftKey);
        });

        if (this.settings.selectAll) {
            $(this.settings.selectAll).on('change.' + pluginName, function () {
                self.setAll(this.checked);
            });
        }

        this.refresh();
    };

    BulkSelect.prototype.boxes = function () {
        return this.$element.find(this.settings.checkbox);
    };

    BulkSelect.prototype.handleClick = function (box, shiftKey) {
        var $boxes = this.boxes();
        var index = $boxes.index(box);

        if (shiftKey && this.lastIndex !== null && this.lastIndex !== index) {
            var start = Math.min(this.lastIndex, index);
            var end = Math.max(this.lastIndex, index);
            var checked = box.checked;
            $boxes.slice(start, end + 1).prop('checked', checked);
        }

        this.lastIndex = index;
        this.refresh();
    };

    BulkSelect.prototype.setAll = function (checked) {
        this.boxes().prop('checked', checked);
        this.lastIndex = null;
        this.refresh();
    };

    BulkSelect.prototype.selected = function () {
        var attr = this.settings.valueAttr;
        return this.boxes().filter(':checked').map(function () {
            return $(this).attr(attr);
        }).get();
    };

    BulkSelect.prototype.refresh = function () {
        var rowClass = this.settings.rowClass;
        var $boxes = this.boxes();

        $boxes.each(function () {
            $(this).closest('tr').toggleClass(rowClass, this.checked);
        });

        if (this.settings.selectAll) {
            var total = $boxes.length;
            var count = $boxes.filter(':checked').length;
            $(this.settings.selectAll)
                .prop('checked', total > 0 && count === total)
                .prop('indeterminate', count > 0 && count < total);
        }

        if (typeof this.settings.onChange === 'function') {
            this.settings.onChange.call(this.$element[0], this.selected());
        }
    };

    BulkSelect.prototype.destroy = function () {
        this.$element.off('.' + pluginName);
        if (this.settings.selectAll) {
            $(this.settings.selectAll).off('.' + pluginName);
        }
        this.$element.removeData(dataKey);
    };

    $.fn[pluginName] = function (options) {
        if (typeof options === 'string') {
            var instance = this.first().data(dataKey);
            if (!instance || typeof instance[options] !== 'function') {
                return undefined;
            }
            return instance[options].apply(instance, Array.prototype.slice.call(arguments, 1));
        }

        return this.each(function () {
            if (!$.data(this, dataKey)) {
                $.data(this, dataKey, new BulkSelect(this, options));
            }
        });
    };

    $.fn[pluginName].defaults = defaults;
}));
