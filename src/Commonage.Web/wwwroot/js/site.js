// Forms marked with data-confirm ask before they are sent, for actions that cannot be undone.
// The question is an attribute rather than inline script, so any text in any language is safe in it.
document.addEventListener('submit', event => {
    const question = event.target.dataset.confirm;
    if (question && !window.confirm(question))
        event.preventDefault();
});
