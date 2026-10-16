(function () {
    "use strict";

    const form = document.querySelector("[data-role-permission-editor]");
    if (!form) return;

    const permissions = Array.from(form.querySelectorAll(
        'input[type="checkbox"][name="PermissionIds"][data-permission-group]'));
    const selectAll = form.querySelector("[data-permissions-select-all]");
    const groupControls = Array.from(form.querySelectorAll("[data-permission-group-all]"));

    function permissionsInGroup(groupName) {
        return permissions.filter(input => input.dataset.permissionGroup === groupName);
    }

    function syncControl(control, inputs) {
        const selectedCount = inputs.reduce((count, input) => count + (input.checked ? 1 : 0), 0);
        const allSelected = inputs.length > 0 && selectedCount === inputs.length;
        const partiallySelected = selectedCount > 0 && !allSelected;

        control.checked = allSelected;
        control.indeterminate = partiallySelected;
        control.setAttribute("aria-checked", partiallySelected ? "mixed" : allSelected ? "true" : "false");
    }

    function syncAllControls() {
        if (selectAll) syncControl(selectAll, permissions);
        groupControls.forEach(control => {
            syncControl(control, permissionsInGroup(control.dataset.permissionGroupAll));
        });
    }

    if (selectAll) {
        selectAll.addEventListener("change", function () {
            permissions.forEach(input => { input.checked = selectAll.checked; });
            syncAllControls();
        });
    }

    groupControls.forEach(control => {
        control.addEventListener("change", function () {
            permissionsInGroup(control.dataset.permissionGroupAll)
                .forEach(input => { input.checked = control.checked; });
            syncAllControls();
        });
    });

    permissions.forEach(input => input.addEventListener("change", syncAllControls));
    form.addEventListener("reset", () => window.requestAnimationFrame(syncAllControls));

    // Also initializes native indeterminate state for server-rendered partial selections.
    syncAllControls();
})();
