// Forms marked with data-confirm ask before they are sent, for actions that cannot be undone.
// The question is an attribute rather than inline script, so any text in any language is safe in it.
document.addEventListener('submit', event => {
    const question = event.target.dataset.confirm;
    if (question && !window.confirm(question))
        event.preventDefault();
});

// Forms marked with data-warn-unsaved make the browser ask before the page is left with changes that nobody saved,
// whether by a link, the language switch or another form on the page. Sending the form itself is not leaving.
// data-warn-unsaved="dirty" means the page came back from the server with text that is still not saved.
const unsavedForms = new Set(document.querySelectorAll('form[data-warn-unsaved="dirty"]'));
let sendingUnsavedForm = false;

for (const type of ['input', 'change']) {
    document.addEventListener(type, event => {
        if (event.target.form?.hasAttribute('data-warn-unsaved'))
            unsavedForms.add(event.target.form);
    });
}
// If validation stopped the form from being sent, the person's next step counts as leaving again.
for (const type of ['pointerdown', 'keydown'])
    document.addEventListener(type, () => { sendingUnsavedForm = false; }, true);
document.addEventListener('submit', event => {
    if (unsavedForms.has(event.target))
        sendingUnsavedForm = true;
});
window.addEventListener('beforeunload', event => {
    if (unsavedForms.size > 0 && !sendingUnsavedForm)
        event.preventDefault();
});
