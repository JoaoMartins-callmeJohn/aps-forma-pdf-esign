let currentView = null;

async function getAccessToken(callback) {
    try {
        const resp = await fetch('/api/auth/token');
        if (!resp.ok)
            throw new Error(await resp.text());
        const { access_token, expires_in } = await resp.json();
        callback(access_token, expires_in);
    } catch (err) {
        alert('Could not obtain access token. See the console for more details.');
        console.error(err);
    }
}

export function initViewer(container) {
    return new Promise(function (resolve, reject) {
        Autodesk.Viewing.Initializer({ env: 'AutodeskProduction', getAccessToken }, function () {
            const viewer = new Autodesk.Viewing.GuiViewer3D(container);
            viewer.start();
            viewer.setTheme('light-theme');
            resolve(viewer);
        });
    });
}

// Loads only the 2D views (sheets, 2D views, PDF pages) of a design, listing them in the given <select>
export function loadModel(viewer, urn, select) {
    function onDocumentLoadSuccess(doc) {
        const views = doc.getRoot().search({ type: 'geometry', role: '2d' });
        select.innerHTML = '';
        if (views.length === 0) {
            currentView = null;
            alert('This design has no 2D views.');
            return;
        }
        views.forEach((view, index) => select.add(new Option(view.name(), index)));
        select.onchange = () => loadView(views[select.value]);
        loadView(views[0]);
    }
    function onDocumentLoadFailure(code, message) {
        alert('Could not load model. See console for more details.');
        console.error(message);
    }
    function loadView(view) {
        currentView = view;
        viewer.loadDocumentNode(view.getDocument(), view);
    }
    currentView = null;
    Autodesk.Viewing.Document.load('urn:' + urn, onDocumentLoadSuccess, onDocumentLoadFailure);
}

export function getCurrentViewName() {
    return currentView ? currentView.name() : null;
}

// URN of the PDF derivative of the current 2D view, if the translation produced one
// (RVT 2022+ or DWG translated with the "2dviews": "pdf" option)
export function getCurrentViewPdfUrn() {
    if (!currentView) {
        return null;
    }
    const pdf = currentView.search({ role: 'pdf-page' })[0];
    return pdf ? pdf.data.urn : null;
}
