const browserSupportsPasskeys =
    typeof navigator.credentials !== 'undefined' &&
    typeof window.PublicKeyCredential !== 'undefined' &&
    typeof window.PublicKeyCredential.parseCreationOptionsFromJSON === 'function' &&
    typeof window.PublicKeyCredential.parseRequestOptionsFromJSON === 'function';

async function fetchWithErrorHandling(url, options, errorMessage) {
    const response = await fetch(url, {
        credentials: 'include',
        ...options
    });
    if (!response.ok) {
        const text = await response.text();
        console.error(text);
        throw new Error(errorMessage.replace('{0}', response.status));
    }
    return response;
}

async function createCredential(signal, serverErrorMessage) {
    const optionsResponse = await fetchWithErrorHandling('/Account/PasskeyCreationOptions', {
        method: 'POST',
        signal,
    }, serverErrorMessage);
    const optionsJson = await optionsResponse.json();
    const options = PublicKeyCredential.parseCreationOptionsFromJSON(optionsJson);
    return await navigator.credentials.create({ publicKey: options, signal });
}

async function requestCredential(email, mediation, signal, serverErrorMessage) {
    const optionsResponse = await fetchWithErrorHandling(`/Account/PasskeyRequestOptions?username=${email}`, {
        method: 'POST',
        signal,
    }, serverErrorMessage);
    const optionsJson = await optionsResponse.json();
    const options = PublicKeyCredential.parseRequestOptionsFromJSON(optionsJson);
    return await navigator.credentials.get({ publicKey: options, mediation, signal });
}

customElements.define('passkey-submit', class extends HTMLElement {
    static formAssociated = true;

    connectedCallback() {
        this.internals = this.attachInternals();
        this.attrs = {
            operation: this.getAttribute('operation'),
            name: this.getAttribute('name'),
            emailName: this.getAttribute('email-name'),
            serverErrorMessage: this.getAttribute('server-error-message'),
            unsupportedBrowserMessage: this.getAttribute('unsupported-browser-message'),
            unknownOperationMessage: this.getAttribute('unknown-operation-message'),
            noPasskeyMessage: this.getAttribute('no-passkey-message'),
        };

        this.internals.form.addEventListener('submit', (event) => {
            if (event.submitter?.name === '__passkeySubmit') {
                event.preventDefault();
                this.obtainAndSubmitCredential();
            }
        });

        this.tryAutofillPasskey();
    }

    disconnectedCallback() {
        this.abortController?.abort();
    }

    async obtainCredential(useConditionalMediation, signal) {
        if (!browserSupportsPasskeys) {
            throw new Error(this.attrs.unsupportedBrowserMessage);
        }

        if (this.attrs.operation === 'Create') {
            return await createCredential(signal, this.attrs.serverErrorMessage);
        } else if (this.attrs.operation === 'Request') {
            const email = new FormData(this.internals.form).get(this.attrs.emailName);
            const mediation = useConditionalMediation ? 'conditional' : undefined;
            return await requestCredential(email, mediation, signal, this.attrs.serverErrorMessage);
        } else {
            throw new Error(this.attrs.unknownOperationMessage.replace('{0}', this.attrs.operation));
        }
    }

    async obtainAndSubmitCredential(useConditionalMediation = false) {
        this.abortController?.abort();
        this.abortController = new AbortController();
        const signal = this.abortController.signal;
        const formData = new FormData();
        try {
            const credential = await this.obtainCredential(useConditionalMediation, signal);
            const credentialJson = JSON.stringify(credential);
            formData.append(`${this.attrs.name}.CredentialJson`, credentialJson);
        } catch (error) {
            if (error.name === 'AbortError') {
                // The user explicitly canceled the operation - return without error.
                return;
            }
            console.error(error);
            if (useConditionalMediation) {
                // An error occurred during conditional mediation, which is not user-initiated.
                // We log the error in the console but do not relay it to the user.
                return;
            }
            const errorMessage = error.name === 'NotAllowedError'
                ? this.attrs.noPasskeyMessage
                : error.message;
            formData.append(`${this.attrs.name}.Error`, errorMessage);
        }
        this.internals.setFormValue(formData);
        this.internals.form.submit();
    }

    async tryAutofillPasskey() {
        if (browserSupportsPasskeys && this.attrs.operation === 'Request' && await PublicKeyCredential.isConditionalMediationAvailable?.()) {
            await this.obtainAndSubmitCredential(/* useConditionalMediation */ true);
        }
    }
});
