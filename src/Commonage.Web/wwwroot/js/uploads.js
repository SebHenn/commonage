// Sends chosen photos one at a time, because a few dozen scans in one request would hit its size limit, and one lost
// connection would lose them all. A file that arrived stays arrived. If the connection goes, the queue waits and goes on
// from where it was. If the page was closed, choosing the same files again is safe: what the archive already has is left
// out and said so. Without JavaScript, the forms still send everything in one request.
{
    const texts = document.getElementById('upload-texts')?.dataset ?? {};
    const say = (text, ...values) => values.reduce((result, value, index) => result.replace(`{${index}}`, value), text ?? '');

    function element(name, className, text) {
        const made = document.createElement(name);
        if (className)
            made.className = className;
        if (text !== undefined)
            made.textContent = text;
        return made;
    }

    for (const form of document.querySelectorAll('form[data-upload-queue]'))
        enhance(form);

    function enhance(form) {
        const input = form.querySelector('input[type=file]');
        const entries = [];
        let sending = false;
        let pausedFor = null;

        const area = element('section', 'mt-3 col-12 col-lg-8');
        area.hidden = true;
        area.setAttribute('aria-label', texts.queueLabel);
        const overall = element('progress', 'upload-progress mb-2');
        overall.max = 1;
        overall.value = 0;
        overall.setAttribute('aria-label', texts.queueLabel);
        const count = element('p', 'mb-1');
        const current = element('p', 'small text-muted text-break mb-2');
        // What a screen reader hears: not every photo out of three hundred, but every so often, and whatever needs a person.
        const spoken = element('p', 'visually-hidden');
        spoken.setAttribute('role', 'status');
        const stop = element('div', 'alert alert-warning');
        stop.hidden = true;
        stop.setAttribute('role', 'alert');
        const stopText = element('p', 'mb-2');
        const loginLink = element('a', 'd-block mb-2', texts.loginLink);
        loginLink.href = '/Account/Login';
        loginLink.target = '_blank';
        loginLink.rel = 'noopener';
        const resume = element('button', 'btn btn-primary', texts.resume);
        resume.type = 'button';
        stop.append(stopText, loginLink, resume);
        const next = element('p', 'mb-2');
        next.hidden = true;
        const nextLink = element('a', 'btn btn-outline-primary', form.dataset.doneText);
        nextLink.href = form.dataset.doneUrl ?? '#';
        next.append(nextLink);
        const list = element('ol', 'upload-queue');
        // Scrolled by keyboard too, for people who do not use a mouse.
        list.tabIndex = 0;
        list.setAttribute('aria-label', texts.queueLabel);
        area.append(overall, count, current, spoken, stop, next, list);
        form.after(area);

        resume.addEventListener('click', go);
        window.addEventListener('online', () => {
            if (pausedFor === 'connection')
                go();
        });
        window.addEventListener('beforeunload', event => {
            if (entries.some(entry => entry.state === 'waiting' || entry.state === 'sending'))
                event.preventDefault();
        });

        form.addEventListener('submit', event => {
            event.preventDefault();
            if (window.jQuery && !window.jQuery(form).valid())
                return;
            // The fields as they are now: changing them for the next batch leaves this one as it was meant.
            const fields = new FormData(form);
            fields.delete('files');
            for (const file of input.files)
                add(file, fields);
            // Later, because ASP.NET's validation runs next and would call the emptied field a mistake.
            setTimeout(() => {
                input.value = '';
                input.dispatchEvent(new Event('change'));
            });
            go();
        });

        function add(file, fields) {
            const row = element('li');
            const thumb = element('span', 'upload-thumb');
            const name = element('span', 'upload-name', file.name);
            const state = element('div', 'upload-state');
            const label = element('span', '', texts.waiting);
            const progress = element('progress', 'upload-file-progress');
            progress.max = 1;
            progress.value = 0;
            progress.setAttribute('aria-label', file.name);
            progress.hidden = true;
            state.append(progress, label);
            row.append(thumb, name, state);
            list.append(row);
            const entry = { file, fields, row, thumb, name, label, progress, sent: 0, addIdentical: false };
            setState(entry, 'waiting');
            entries.push(entry);
            area.hidden = false;
        }

        function setState(entry, state, text) {
            entry.state = state;
            entry.row.dataset.state = state;
            if (text !== undefined)
                entry.label.textContent = text;
        }

        function update(speak) {
            const finished = entries.filter(entry => !['waiting', 'sending'].includes(entry.state)).length;
            const total = entries.reduce((sum, entry) => sum + entry.file.size, 0);
            const sent = entries.reduce((sum, entry) => sum + entry.sent, 0);
            overall.value = total === 0 ? 0 : sent / total;
            const line = say(texts.count, finished, entries.length);
            count.textContent = line;
            if (speak)
                spoken.textContent = line;
        }

        function pause(reason, message) {
            pausedFor = reason;
            stopText.textContent = message;
            loginLink.hidden = reason !== 'login';
            resume.hidden = reason === 'full';
            stop.hidden = false;
            current.textContent = '';
        }

        async function go() {
            pausedFor = null;
            stop.hidden = true;
            next.hidden = true;
            if (sending)
                return;
            sending = true;
            let entry;
            while (pausedFor === null && (entry = entries.find(candidate => candidate.state === 'waiting'))) {
                await send(entry);
                const done = entries.filter(candidate => !['waiting', 'sending'].includes(candidate.state)).length;
                update(done % 25 === 0);
            }
            sending = false;
            if (pausedFor === null)
                finish();
        }

        function finish() {
            const number = state => entries.filter(entry => entry.state === state).length;
            const line = say(texts.done, number('added'), number('identical'), number('refused'));
            count.textContent = line;
            spoken.textContent = line;
            current.textContent = '';
            next.hidden = !form.dataset.doneUrl || number('added') === 0;
            list.scrollTop = 0;
        }

        function send(entry) {
            return new Promise(resolve => {
                setState(entry, 'sending', texts.sending);
                entry.sent = 0;
                entry.progress.value = 0;
                entry.progress.hidden = false;
                current.textContent = say(texts.sentNow, entry.file.name);
                list.scrollTop = entry.row.offsetTop - list.clientHeight / 2;

                const body = new FormData();
                for (const [key, value] of entry.fields)
                    body.append(key, value);
                body.append('files', entry.file, entry.file.name);
                if (entry.addIdentical)
                    body.set('addIdentical', 'true');

                const request = new XMLHttpRequest();
                request.open('POST', form.action);
                request.setRequestHeader('Accept', 'application/json');
                request.upload.addEventListener('progress', event => {
                    if (!event.lengthComputable)
                        return;
                    entry.progress.value = event.total === 0 ? 0 : event.loaded / event.total;
                    entry.sent = entry.file.size * entry.progress.value;
                    update(false);
                });

                const settle = () => {
                    entry.progress.hidden = true;
                    resolve();
                };
                const again = (reason, message) => {
                    setState(entry, 'waiting', texts.waiting);
                    entry.sent = 0;
                    pause(reason, message);
                    settle();
                };
                request.addEventListener('error', () => again('connection', texts.connection));
                request.addEventListener('abort', () => again('connection', texts.connection));
                request.addEventListener('load', async () => {
                    // A login that ran out sends the browser to the login page, which looks like success.
                    if (new URL(request.responseURL, location.href).pathname.startsWith('/Account/'))
                        return again('login', texts.login);
                    let reply = null;
                    try {
                        reply = JSON.parse(request.responseText);
                    } catch {
                        // Not an answer from the page itself; the status says what happened.
                    }
                    if (request.status === 507)
                        return again('full', texts.full);
                    if (request.status === 413) {
                        entry.sent = entry.file.size;
                        setState(entry, 'refused', say(texts.tooLarge, entry.file.name));
                        list.prepend(entry.row);
                        return settle();
                    }
                    if (request.status === 400 && reply?.errors)
                        return again('rejected', reply.errors.join(' '));
                    if (request.status >= 500)
                        return again('connection', texts.connection);
                    if (request.status !== 200 || !reply)
                        return again('rejected', texts.rejected);

                    entry.sent = entry.file.size;
                    if (reply.outcome === 'added')
                        await added(entry, reply);
                    else if (reply.outcome === 'identical')
                        identical(entry, reply);
                    else {
                        setState(entry, 'refused', reply.message);
                        list.prepend(entry.row);
                    }
                    settle();
                });
                request.send(body);
            });
        }

        async function added(entry, reply) {
            setState(entry, 'added', texts.added);
            if (reply.itemUrl) {
                const link = element('a', 'upload-name', entry.file.name);
                link.href = reply.itemUrl;
                link.target = '_blank';
                entry.name.replaceWith(link);
                entry.name = link;
            }
            entry.thumb.dataset.previewPending = reply.mediaFileId;
            entry.thumb.dataset.previewClass = 'upload-thumb';
            entry.thumb.dataset.previewAlt = '';
            if (reply.cardUrl) {
                try {
                    const card = await fetch(reply.cardUrl);
                    if (card.ok)
                        document.getElementById('photo-cards')?.insertAdjacentHTML('beforeend', await card.text());
                    document.querySelector('[data-photos-present]')?.removeAttribute('hidden');
                } catch {
                    // The photo arrived. Its card shows up with the next page load.
                }
            }
            watchPreviews();
        }

        function identical(entry, reply) {
            setState(entry, 'identical', '');
            const link = element('a', '', reply.message);
            link.href = reply.itemUrl;
            link.target = '_blank';
            const button = element('button', 'btn btn-sm btn-outline-secondary ms-2', texts.addAnyway);
            button.type = 'button';
            button.addEventListener('click', () => {
                entry.addIdentical = true;
                setState(entry, 'waiting', texts.waiting);
                go();
            });
            entry.label.append(link, button);
            list.prepend(entry.row);
        }
    }

    // Previews are made in the background. They replace their placeholders as they get done, without a reload.
    let watching = false;

    function watchPreviews() {
        if (!watching && document.querySelector('[data-preview-pending]')) {
            watching = true;
            setTimeout(checkPreviews, 1500);
        }
    }

    async function checkPreviews() {
        const pending = [...document.querySelectorAll('[data-preview-pending]')];
        const status = document.querySelector('[data-preview-status]');
        if (pending.length === 0) {
            watching = false;
            if (status)
                status.textContent = '';
            return;
        }

        try {
            const asked = pending.slice(0, 200);
            const response = await fetch(`/admin/media/status?ids=${asked.map(placeholder => placeholder.dataset.previewPending).join(',')}`);
            const answer = await response.json();
            for (const placeholder of asked) {
                const id = Number(placeholder.dataset.previewPending);
                if (answer.ready.includes(id)) {
                    const picture = new Image();
                    picture.src = `/admin/media/${id}/thumbnail`;
                    picture.alt = placeholder.dataset.previewAlt ?? '';
                    picture.className = placeholder.dataset.previewClass;
                    placeholder.replaceWith(picture);
                } else if (answer.failed.includes(id)) {
                    placeholder.textContent = placeholder.dataset.previewFailed ?? '';
                    delete placeholder.dataset.previewPending;
                } else if (!answer.waiting.includes(id)) {
                    // Moved to the bin in the meantime, for example.
                    delete placeholder.dataset.previewPending;
                }
            }
            if (status)
                status.textContent = answer.message;
            setTimeout(checkPreviews, 3000);
        } catch {
            // Logged out or offline. Only a convenience: reloading the page shows them too.
            watching = false;
        }
    }

    watchPreviews();

    // A new item's photos go with its form, in one request. If that cannot work, it is better said before anything is sent.
    for (const input of document.querySelectorAll('input[type=file][data-max-total-bytes]')) {
        input.form.addEventListener('submit', event => {
            const total = [...input.files].reduce((sum, file) => sum + file.size, 0);
            if (total <= Number(input.dataset.maxTotalBytes))
                return;
            event.preventDefault();
            // Later, because ASP.NET's validation runs next and empties the message of a field it finds valid.
            setTimeout(() => {
                const message = input.form.querySelector('[data-valmsg-for="files"]');
                if (message)
                    message.textContent = input.dataset.tooMuchText;
            });
        });
    }
}
