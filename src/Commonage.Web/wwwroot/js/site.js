// Forms marked with data-confirm ask before they are sent, for actions that cannot be undone.
// The question is an attribute rather than inline script, so any text in any language is safe in it.
document.addEventListener('submit', event => {
    const question = event.target.dataset.confirm;
    if (question && !window.confirm(question))
        event.preventDefault();
});

// A form that came back with errors: the summary of them gets the focus, so it is read out and in view.
document.querySelector('[data-focus-on-load]')?.focus();

// Links each field to its error message, so a screen reader also reads messages that appear without a page load.
for (const message of document.querySelectorAll('[data-valmsg-for]')) {
    if (!message.id)
        message.id = `${message.dataset.valmsgFor.replaceAll('.', '_')}-error`;
    for (const field of document.getElementsByName(message.dataset.valmsgFor)) {
        if (field.type === 'hidden')
            continue;
        const described = field.getAttribute('aria-describedby')?.split(' ') ?? [];
        if (!described.includes(message.id))
            field.setAttribute('aria-describedby', [...described, message.id].join(' '));
    }
}

// A required checkbox. ASP.NET's validation leaves checkboxes out, and sending the form to find out would lose chosen files.
document.addEventListener('submit', event => {
    for (const box of event.target.querySelectorAll('input[type=checkbox][data-required-message]')) {
        if (box.checked)
            continue;
        event.preventDefault();
        // Later, because ASP.NET's validation runs next and empties the message of a field it finds valid.
        setTimeout(() => {
            const message = event.target.querySelector(`[data-valmsg-for="${box.name}"]`);
            if (message)
                message.textContent = box.dataset.requiredMessage;
        });
    }
}, true);

// A form sent once stays sent: a second click would upload everything again, or look like someone else's change.
document.addEventListener('submit', event => {
    const form = event.target;
    if (!form.hasAttribute('data-send-once'))
        return;
    if (form.dataset.sending) {
        event.preventDefault();
        return;
    }
    if (event.defaultPrevented)
        return;
    form.dataset.sending = 'true';
    // Later, because a button disabled now would leave its value out of what is sent.
    setTimeout(() => form.querySelectorAll('button[type=submit]').forEach(button => { button.disabled = true; }));
});
// The back button can bring a page back exactly as it was left.
window.addEventListener('pageshow', () => {
    for (const form of document.querySelectorAll('form[data-send-once]')) {
        delete form.dataset.sending;
        form.querySelectorAll('button[type=submit]').forEach(button => { button.disabled = false; });
    }
});

// Says how a date will be understood while it is typed. Without JavaScript, the form says so after sending.
for (const input of document.querySelectorAll('input[data-date-result]')) {
    const result = document.getElementById(input.dataset.dateResult);
    let timer;
    let latest = 0;
    input.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(async () => {
            const asked = ++latest;
            const text = input.value.trim();
            if (!text) {
                result.textContent = '';
                result.classList.remove('text-danger');
                return;
            }
            try {
                const response = await fetch(`/date-check?text=${encodeURIComponent(text)}`);
                if (!response.ok || asked !== latest)
                    return;
                const answer = await response.json();
                result.textContent = answer.message;
                result.classList.toggle('text-danger', !answer.understood);
            } catch {
                // Only a convenience: the form still checks the date when it is sent.
            }
        }, 400);
    });
}

// The browser's own file control speaks the browser's language, not the page's. This one speaks the page's.
for (const input of document.querySelectorAll('input[type=file][data-choose-text]')) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-outline-secondary';
    button.textContent = input.dataset.chooseText;
    button.addEventListener('click', () => input.click());

    const chosen = document.createElement('span');
    chosen.id = `${input.id}-chosen`;
    chosen.className = 'text-break align-self-center';
    chosen.setAttribute('role', 'status');
    chosen.textContent = input.dataset.noneText;
    button.setAttribute('aria-describedby', chosen.id);

    const picker = document.createElement('span');
    picker.className = 'd-flex flex-wrap gap-2';
    picker.append(button, chosen);
    input.after(picker);

    input.classList.remove('form-control', 'w-auto');
    input.classList.add('visually-hidden');
    input.tabIndex = -1;
    input.addEventListener('change', () => {
        const names = [...input.files].map(file => file.name);
        chosen.textContent = names.length > 5 && input.dataset.countText
            ? input.dataset.countText.replace('{0}', names.length)
            : names.join(', ') || input.dataset.noneText;
    });
}

// A box to tick all boxes of a list. It only exists with JavaScript, because only JavaScript can make it work.
for (const all of document.querySelectorAll('input[data-tick-all]')) {
    const boxes = () => [...document.getElementsByName(all.dataset.tickAll)];
    all.hidden = false;
    all.addEventListener('change', () => boxes().forEach(box => { box.checked = all.checked; }));
    document.addEventListener('change', event => {
        if (event.target.name === all.dataset.tickAll)
            all.checked = boxes().every(box => box.checked);
    });
}

// The language menu closes like any other menu: by clicking elsewhere or pressing Escape.
document.addEventListener('click', event => {
    for (const menu of document.querySelectorAll('details.language-picker[open]')) {
        if (!menu.contains(event.target))
            menu.open = false;
    }
});
document.addEventListener('keydown', event => {
    if (event.key === 'Escape')
        document.querySelectorAll('details.language-picker[open]').forEach(menu => { menu.open = false; });
});

// A choice in a list takes effect at once. Without JavaScript, the form's button does the same.
document.addEventListener('change', event => {
    if (event.target.hasAttribute('data-autosubmit'))
        event.target.form.requestSubmit();
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
