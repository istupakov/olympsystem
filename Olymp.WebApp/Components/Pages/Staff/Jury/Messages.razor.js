const instances = new Map();
let nextId = 0;

export function initialize(button, status, source) {
    const id = ++nextId;
    const audio = new Audio(new URL(source, document.baseURI));
    audio.preload = 'auto';
    let enabled = false;
    const update = () => {
        button.textContent = enabled ? button.dataset.disableLabel : button.dataset.enableLabel;
        button.setAttribute('aria-pressed', String(enabled));
    };
    const play = async () => {
        try {
            audio.currentTime = 0;
            await audio.play();
            status.textContent = '';
        } catch {
            enabled = false;
            status.textContent = button.dataset.errorLabel;
            update();
        }
    };
    const toggle = () => {
        enabled = !enabled;
        status.textContent = '';
        if (enabled)
            play();
        else
            audio.pause();
        update();
    };
    button.addEventListener('click', toggle);
    const observer = new MutationObserver(() => {
        if (!button.isConnected)
            dispose(id);
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    instances.set(id, {
        notify: () => enabled ? play() : Promise.resolve(),
        dispose: () => {
            observer.disconnect();
            button.removeEventListener('click', toggle);
            audio.pause();
            audio.removeAttribute('src');
            audio.load();
        }
    });
    return id;
}

export async function notify(id) {
    await instances.get(id)?.notify();
}

export function dispose(id) {
    instances.get(id)?.dispose();
    instances.delete(id);
}
