// What makes the contribution form pleasant on a phone with a weak connection: the chosen photos can be seen and removed,
// mistakes are found before anything is sent, sending shows its progress and happens once, and nothing is lost when it
// fails. Without JavaScript, the form still works: it is sent the ordinary way, in one go.
{
    const form = document.querySelector('form[data-contribution]');
    const texts = document.getElementById('contribute-texts')?.dataset ?? {};
    const say = (text, ...values) => values.reduce((result, value, index) => result.replace(`{${index}}`, value), text ?? '');

    function element(name, className, text) {
        const made = document.createElement(name);
        if (className)
            made.className = className;
        if (text !== undefined)
            made.textContent = text;
        return made;
    }

    if (form) {
        const photos = choosePhotos();
        const draft = keepDraft();
        showRightsHolderWhenItApplies();
        sendWithProgress(photos, draft);
    }

    function choosePhotos() {
        const input = form.querySelector('#Files');
        const picker = input.nextElementSibling;
        const chooser = picker?.querySelector('button');
        const label = document.getElementById(`${input.id}-chosen`);
        chooser?.classList.add('flex-grow-1', 'flex-sm-grow-0');

        const list = element('ul', 'chosen-photos');
        list.hidden = true;
        const problems = element('div', 'text-danger mt-2');
        problems.setAttribute('role', 'alert');
        (picker ?? input).after(list, problems);

        const chosen = [];
        // Files are looked at one after the other, and sending waits for that.
        let looking = Promise.resolve();

        input.addEventListener('change', () => {
            const picked = [...input.files];
            // So that a photo removed from the list can be chosen again: browsers stay silent when the choice looks unchanged.
            input.value = '';
            problems.replaceChildren();
            looking = looking.then(async () => {
                for (const file of picked)
                    await consider(file);
                update();
            });
        });

        async function consider(file) {
            const key = `${file.name}/${file.size}/${file.lastModified}`;
            if (chosen.some(photo => photo.key === key))
                return;
            const problem = await problemWith(file);
            if (!problem)
                add(file, key);
            else if (![...problems.children].some(shown => shown.textContent === problem))
                problems.append(element('p', 'mb-1', problem));
        }

        async function problemWith(file) {
            if (file.size === 0)
                return say(texts.empty, file.name);
            if (file.size > Number(input.dataset.maxFileBytes))
                return say(texts.tooLarge, file.name);
            const format = await formatOf(file);
            if (format === 'heic')
                return say(texts.heic, file.name);
            if (format === 'other')
                return say(texts.unsupported, file.name);
            if (chosen.length >= Number(input.dataset.maxFiles))
                return texts.tooMany;
            if (totalBytes() + file.size > Number(input.dataset.maxRequestBytes))
                return say(texts.doesNotFit, file.name);
            return null;
        }

        function totalBytes() {
            return chosen.reduce((sum, photo) => sum + photo.file.size, 0);
        }

        function add(file, key) {
            const row = element('li');
            const url = URL.createObjectURL(file);
            const picture = new Image();
            picture.className = 'chosen-photo-picture';
            picture.alt = '';
            picture.decoding = 'async';
            picture.src = url;
            // A TIFF, for example: browsers can send it, but most cannot show it. The name says which photo it is.
            picture.addEventListener('error', () => picture.replaceWith(element('span', 'chosen-photo-picture')));
            const remove = element('button', 'btn btn-sm btn-outline-secondary', texts.remove);
            remove.type = 'button';
            remove.setAttribute('aria-label', say(texts.removeNamed, file.name));
            remove.addEventListener('click', () => {
                const position = chosen.findIndex(photo => photo.key === key);
                chosen.splice(position, 1);
                URL.revokeObjectURL(url);
                row.remove();
                problems.replaceChildren();
                update();
                (chosen[position] ?? chosen[position - 1])?.remove.focus();
                if (chosen.length === 0)
                    chooser?.focus();
            });
            row.append(picture, element('span', 'upload-name small', file.name), remove);
            list.append(row);
            chosen.push({ file, key, remove });
        }

        function update() {
            list.hidden = chosen.length === 0;
            if (label)
                label.textContent = chosen.length === 0 ? input.dataset.noneText : say(input.dataset.countText, chosen.length);
            if (chooser)
                chooser.textContent = chosen.length === 0 ? input.dataset.chooseText : texts.addMore;
        }

        return {
            ready: () => looking,
            files: () => chosen.map(photo => photo.file),
        };
    }

    // The same check as the server's ImageFormats: the first bytes say what a file is, not its name.
    async function formatOf(file) {
        let bytes;
        try {
            bytes = new Uint8Array(await file.slice(0, 12).arrayBuffer());
        } catch {
            // Left to the archive to find out.
            return 'photo';
        }
        const text = (from, to) => String.fromCharCode(...bytes.slice(from, to));
        const starts = (...values) => values.every((value, index) => bytes[index] === value);
        if (starts(0xFF, 0xD8, 0xFF)
            || starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
            || starts(0x49, 0x49, 0x2A, 0x00)
            || starts(0x4D, 0x4D, 0x00, 0x2A)
            || (text(0, 4) === 'RIFF' && text(8, 12) === 'WEBP'))
            return 'photo';
        if (text(4, 8) === 'ftyp' && ['heic', 'heix', 'hevc', 'hevx', 'mif1', 'msf1'].includes(text(8, 12)))
            return 'heic';
        return 'other';
    }

    // Title, date and description stay on this device for a while, so that a call or an empty battery costs nothing. Never
    // name, email address or the photographer's name: at an event, the next person may be using the same device.
    function keepDraft() {
        const prefix = 'commonage-contribution-draft:';
        const key = prefix + location.pathname;
        const maxAge = 3 * 24 * 60 * 60 * 1000;
        const fields = ['Input.Title', 'Input.Date', 'Input.Description'].map(name => form.elements[name]);
        const [title, date, description] = fields;

        const note = element('div', 'draft-note form-text mb-3');
        const noteText = element('span', 'me-2');
        const clear = element('button', 'btn btn-sm btn-outline-secondary', texts.draftClear);
        clear.type = 'button';
        note.append(noteText, clear);
        note.hidden = true;

        // Storage can be switched off, or throw in a private window. Then there is simply no draft.
        const storage = {
            read(name) {
                try {
                    return JSON.parse(localStorage.getItem(name));
                } catch {
                    return null;
                }
            },
            write(name, value) {
                try {
                    localStorage.setItem(name, JSON.stringify(value));
                } catch {
                    // See above.
                }
            },
            remove(name) {
                try {
                    localStorage.removeItem(name);
                } catch {
                    // See above.
                }
            },
            names() {
                try {
                    return Object.keys(localStorage);
                } catch {
                    return [];
                }
            },
        };

        // Also those of other contribution links on this device, which might never be opened again.
        for (const name of storage.names().filter(name => name.startsWith(prefix))) {
            const old = storage.read(name);
            if (!old || !(Date.now() - old.savedAt < maxAge))
                storage.remove(name);
        }

        const hasText = () => fields.some(field => field.value.trim() !== '');
        const earlier = storage.read(key);
        let restored = false;
        // A form that came back from the server already holds what was sent.
        if (earlier && !hasText()) {
            title.value = earlier.title ?? '';
            date.value = earlier.date ?? '';
            description.value = earlier.description ?? '';
            restored = hasText();
            date.dispatchEvent(new Event('date-filled'));
        }

        if (restored) {
            title.closest('.mb-3').before(note);
            noteText.textContent = texts.draftRestored;
            note.hidden = false;
        } else {
            description.closest('.mb-4').append(note);
            noteText.textContent = texts.draftKept;
        }

        let timer;
        function save() {
            clearTimeout(timer);
            if (!hasText()) {
                storage.remove(key);
                note.hidden = true;
                return;
            }
            storage.write(key, { title: title.value, date: date.value, description: description.value, savedAt: Date.now() });
            note.hidden = false;
        }

        for (const field of fields) {
            field.addEventListener('input', () => {
                clearTimeout(timer);
                timer = setTimeout(save, 400);
            });
        }
        window.addEventListener('pagehide', () => {
            if (timer !== undefined)
                save();
        });

        clear.addEventListener('click', () => {
            clearTimeout(timer);
            timer = undefined;
            for (const field of fields)
                field.value = '';
            storage.remove(key);
            note.hidden = true;
            date.dispatchEvent(new Event('date-filled'));
            title.focus();
        });

        return {
            forget() {
                clearTimeout(timer);
                timer = undefined;
                storage.remove(key);
            },
        };
    }

    function showRightsHolderWhenItApplies() {
        const holder = form.querySelector('[data-rights-holder]');
        const radios = form.querySelectorAll('input[name="Input.Rights"]');
        const field = holder.querySelector('input');
        const update = () => {
            const applies = form.querySelector('input[name="Input.Rights"]:checked')?.value === 'PermissionFromRightsHolder';
            holder.hidden = !applies;
            // What cannot be seen is not sent.
            field.disabled = !applies;
        };
        radios.forEach(radio => radio.addEventListener('change', update));
        update();
    }

    function sendWithProgress(photos, draft) {
        const button = form.querySelector('button[type=submit]');
        let request = null;

        const area = element('section', 'contribute-sending mt-3');
        area.hidden = true;
        area.tabIndex = -1;
        area.setAttribute('aria-label', texts.sendingLabel);
        const bar = element('progress', 'upload-progress mb-2');
        bar.max = 1;
        bar.value = 0;
        bar.setAttribute('aria-label', texts.sendingLabel);
        const progress = element('p', 'mb-1');
        const keepOpen = element('p', 'small text-muted mb-2', texts.keepOpen);
        // Read out now and then, not with every step.
        const spoken = element('p', 'visually-hidden');
        spoken.setAttribute('role', 'status');
        const stop = element('button', 'btn btn-outline-secondary', texts.stop);
        stop.type = 'button';
        area.append(bar, progress, keepOpen, spoken, stop);
        const failure = element('p', 'alert alert-warning mt-3 mb-0');
        failure.setAttribute('role', 'alert');
        failure.hidden = true;
        form.after(area, failure);

        const megabytes = new Intl.NumberFormat(document.documentElement.lang || undefined, {
            style: 'unit',
            unit: 'megabyte',
            maximumFractionDigits: 1,
        });
        const inMegabytes = bytes => megabytes.format(bytes / (1024 * 1024));

        window.addEventListener('beforeunload', event => {
            if (request)
                event.preventDefault();
        });

        form.addEventListener('submit', async event => {
            // Stopped by site.js already: the consent is missing.
            const stoppedAlready = event.defaultPrevented;
            event.preventDefault();
            if (form.dataset.sending || stoppedAlready || (window.jQuery && !window.jQuery(form).valid()))
                return;

            form.dataset.sending = 'true';
            clearMistakes();
            failure.hidden = true;
            await photos.ready();
            const dateProblem = await problemWithDate();
            if (dateProblem) {
                delete form.dataset.sending;
                showMistakes({ 'Input.Date': [dateProblem] });
                return;
            }
            send();
        });

        // Finds a wrong date before the photos make their way over a slow connection.
        async function problemWithDate() {
            const date = form.elements['Input.Date'].value.trim();
            if (!date)
                return null;
            try {
                const response = await fetch(`/date-check?text=${encodeURIComponent(date)}`);
                if (!response.ok)
                    return null;
                const answer = await response.json();
                return answer.understood ? null : answer.message;
            } catch {
                // The archive checks it again when it gets there.
                return null;
            }
        }

        function send() {
            const body = new FormData(form);
            body.delete('Files');
            for (const file of photos.files())
                body.append('Files', file, file.name);

            const current = request = new XMLHttpRequest();
            let endedBy = null;
            let lastSpoken = 0;
            let watchdog;
            // A connection can go quiet without ever failing. On a phone, that looks like it is still sending, forever.
            const watch = () => {
                clearTimeout(watchdog);
                watchdog = setTimeout(() => {
                    endedBy = 'silence';
                    current.abort();
                }, 90000);
            };

            current.open('POST', form.action);
            current.setRequestHeader('Accept', 'application/json');
            current.upload.addEventListener('progress', event => {
                watch();
                if (!event.lengthComputable || event.total === 0)
                    return;
                const share = event.loaded / event.total;
                bar.value = share;
                progress.textContent = say(texts.progress, inMegabytes(event.loaded), inMegabytes(event.total));
                if (share - lastSpoken >= 0.25) {
                    lastSpoken = share;
                    spoken.textContent = `${Math.round(share * 100)} %`;
                }
            });
            current.upload.addEventListener('load', () => {
                clearTimeout(watchdog);
                bar.value = 1;
                progress.textContent = texts.saving;
                spoken.textContent = texts.saving;
                // Everything is on its way to being saved, so stopping now would only be a false promise.
                stop.hidden = true;
            });
            const finish = message => {
                clearTimeout(watchdog);
                request = null;
                delete form.dataset.sending;
                form.inert = false;
                area.hidden = true;
                button.disabled = false;
                if (message) {
                    failure.textContent = message;
                    failure.hidden = false;
                    failure.scrollIntoView({ block: 'nearest' });
                    button.focus();
                }
            };
            stop.onclick = () => {
                endedBy = 'person';
                current.abort();
            };
            current.addEventListener('error', () => finish(texts.connection));
            current.addEventListener('abort', () => finish(endedBy === 'person' ? texts.stopped : texts.connection));
            current.addEventListener('load', () => answered(current, finish));

            bar.value = 0;
            // Already there, so that everything that belongs together is scrolled into view below.
            const total = photos.files().reduce((sum, file) => sum + file.size, 0);
            progress.textContent = say(texts.progress, inMegabytes(0), inMegabytes(total));
            stop.hidden = false;
            area.hidden = false;
            button.disabled = true;
            // Nothing on the form can change any more: what is on its way is what counts.
            form.inert = true;
            area.focus();
            area.scrollIntoView({ block: 'nearest' });
            watch();
            current.send(body);
        }

        function answered(current, finish) {
            let reply = null;
            try {
                reply = JSON.parse(current.responseText);
            } catch {
                // Not an answer from the page itself; the status says what happened.
            }

            if (current.status === 200 && reply?.thanksUrl) {
                draft.forget();
                request = null;
                // There is nothing left to warn about, whatever was typed on the way.
                form.removeAttribute('data-warn-unsaved');
                // Not in the browser's history, so going back does not lead to the sent form.
                location.replace(reply.thanksUrl);
            } else if (current.status === 400 && reply?.errors) {
                finish(null);
                showMistakes(reply.errors);
            } else if (current.status === 429) {
                finish(current.responseText);
            } else if (current.status === 413) {
                finish(texts.tooMuch);
            } else if (current.status === 507) {
                finish(texts.full);
            } else if (current.status >= 500) {
                finish(texts.connection);
            } else {
                finish(texts.rejected);
            }
        }

        // The same as the summary a page gets from the server, which is the only other way to see these.
        function showMistakes(errors) {
            clearMistakes();
            const summary = element('div', 'alert alert-danger');
            summary.dataset.mistakes = '';
            summary.setAttribute('role', 'alert');
            summary.tabIndex = -1;
            summary.append(element('p', 'fw-bold mb-1', texts.check));
            const list = element('ul', 'mb-0');
            for (const [field, messages] of Object.entries(errors)) {
                const control = form.elements[field];
                const single = control && !(control instanceof RadioNodeList);
                if (single) {
                    control.setAttribute('aria-invalid', 'true');
                    control.dataset.mistake = '';
                }
                // A date has a place for what is known about it, which changes as soon as someone corrects it.
                const message = single && control.dataset.dateResult
                    ? document.getElementById(control.dataset.dateResult)
                    : form.querySelector(`[data-valmsg-for="${CSS.escape(field)}"]`);
                if (message) {
                    message.textContent = messages.join(' ');
                    message.classList.add('text-danger');
                    message.classList.remove('field-validation-valid');
                    message.classList.add('field-validation-error');
                    message.dataset.mistake = '';
                }
                for (const text of messages) {
                    const row = element('li');
                    const link = element('a', 'alert-link', text);
                    link.href = `#${field.replaceAll('.', '_')}`;
                    row.append(link);
                    list.append(row);
                }
            }
            summary.append(list);
            form.prepend(summary);
            summary.focus();
        }

        function clearMistakes() {
            form.querySelector('[data-mistakes]')?.remove();
            for (const marked of form.querySelectorAll('[data-mistake]')) {
                delete marked.dataset.mistake;
                if (marked.matches('input, textarea, select')) {
                    marked.removeAttribute('aria-invalid');
                } else {
                    marked.textContent = '';
                    marked.classList.remove('field-validation-error');
                    marked.classList.add('field-validation-valid');
                }
            }
        }
    }
}
