use leptos::prelude::*;

use crate::types::McpServer;

/// Create a new server.
#[server]
pub async fn create_server(
    name: String,
    slug: String,
    url: String,
    auth_header: String,
) -> Result<McpServer, ServerFnError> {
    use crate::server::db;
    let pool = db::pool();

    // Validate slug format
    if slug.is_empty()
        || !slug
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
    {
        return Err(ServerFnError::new(
            "Slug must be non-empty and contain only letters, numbers, hyphens, and underscores",
        ));
    }

    // Check for duplicate slug
    if db::get_server_by_slug(pool, &slug)
        .await
        .map_err(|e| ServerFnError::new(format!("{e}")))?
        .is_some()
    {
        return Err(ServerFnError::new(format!(
            "A server with slug '{slug}' already exists"
        )));
    }

    let url_opt = if url.is_empty() {
        None
    } else {
        Some(url.as_str())
    };
    let auth_opt = if auth_header.is_empty() {
        None
    } else {
        Some(auth_header.as_str())
    };

    db::create_server(pool, &slug, &name, url_opt, auth_opt)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

/// Update an existing server.
#[server]
pub async fn update_server(
    id: String,
    name: String,
    slug: String,
    url: String,
    auth_header: String,
) -> Result<(), ServerFnError> {
    use crate::server::db;
    let pool = db::pool();

    // Validate slug format
    if slug.is_empty()
        || !slug
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
    {
        return Err(ServerFnError::new(
            "Slug must be non-empty and contain only letters, numbers, hyphens, and underscores",
        ));
    }

    // Check slug isn't taken by another server
    if let Some(existing) = db::get_server_by_slug(pool, &slug)
        .await
        .map_err(|e| ServerFnError::new(format!("{e}")))?
    {
        if existing.id != id {
            return Err(ServerFnError::new(format!(
                "A server with slug '{slug}' already exists"
            )));
        }
    }

    let url_opt = if url.is_empty() {
        None
    } else {
        Some(url.as_str())
    };
    let auth_opt = if auth_header.is_empty() {
        None
    } else {
        Some(auth_header.as_str())
    };

    db::update_server(pool, &id, &slug, &name, url_opt, auth_opt)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

/// Test connection to an MCP server before saving.
/// Returns a description of what was found (server name, tool count).
#[server]
pub async fn test_mcp_connection(
    url: String,
    auth_header: String,
) -> Result<String, ServerFnError> {
    use crate::server::upstream::UpstreamManager;

    if url.is_empty() {
        return Err(ServerFnError::new("URL is required to test a connection"));
    }

    let auth_opt = if auth_header.is_empty() {
        None
    } else {
        Some(auth_header.as_str())
    };

    let result = UpstreamManager::test_connection(&url, auth_opt)
        .await
        .map_err(|e| ServerFnError::new(e))?;

    Ok(format!(
        "Connected to {} v{}. Found {} tool(s): {}",
        result.server_name,
        result.server_version,
        result.tool_count,
        if result.tool_names.is_empty() {
            "(none)".to_string()
        } else {
            result.tool_names.join(", ")
        }
    ))
}

/// Generate a slug from a display name.
fn slugify(name: &str) -> String {
    name.to_lowercase()
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect::<String>()
        .split('-')
        .filter(|s| !s.is_empty())
        .collect::<Vec<_>>()
        .join("-")
}

#[component]
pub fn ServerForm(
    /// If Some, we're editing an existing server. If None, creating a new one.
    #[prop(optional)]
    server: Option<McpServer>,
    /// Called when the form is submitted successfully.
    #[prop(into)]
    on_saved: Callback<()>,
    /// Called when the user cancels.
    #[prop(into)]
    on_cancel: Callback<()>,
) -> impl IntoView {
    let is_edit = server.is_some();
    let server_id = server.as_ref().map(|s| s.id.clone()).unwrap_or_default();

    let (name, set_name) = signal(server.as_ref().map(|s| s.name.clone()).unwrap_or_default());
    let (slug, set_slug) = signal(server.as_ref().map(|s| s.slug.clone()).unwrap_or_default());
    let (url, set_url) = signal(
        server
            .as_ref()
            .and_then(|s| s.url.clone())
            .unwrap_or_default(),
    );
    let (auth_header, set_auth_header) = signal(
        server
            .as_ref()
            .and_then(|s| s.auth_header.clone())
            .unwrap_or_default(),
    );
    let (error, set_error) = signal(Option::<String>::None);
    let (success_msg, set_success_msg) = signal(Option::<String>::None);
    let (submitting, set_submitting) = signal(false);
    let (testing, set_testing) = signal(false);

    // Auto-generate slug from name (only for new servers)
    let (slug_manually_edited, set_slug_manually_edited) = signal(is_edit);

    let on_name_input = move |ev: leptos::ev::Event| {
        let value = event_target_value(&ev);
        set_name.set(value.clone());
        if !slug_manually_edited.get_untracked() {
            set_slug.set(slugify(&value));
        }
    };

    let on_slug_input = move |ev: leptos::ev::Event| {
        set_slug_manually_edited.set(true);
        set_slug.set(event_target_value(&ev));
    };

    let on_submit = move |ev: leptos::ev::SubmitEvent| {
        ev.prevent_default();
        set_error.set(None);
        set_success_msg.set(None);
        set_submitting.set(true);

        let id = server_id.clone();
        let name_val = name.get_untracked();
        let slug_val = slug.get_untracked();
        let url_val = url.get_untracked();
        let auth_val = auth_header.get_untracked();
        let on_saved = on_saved.clone();

        leptos::task::spawn_local(async move {
            // For new servers with a URL, test the connection first
            if !is_edit && !url_val.is_empty() {
                match test_mcp_connection(url_val.clone(), auth_val.clone()).await {
                    Ok(_) => {} // Connection valid, proceed to save
                    Err(e) => {
                        set_submitting.set(false);
                        set_error.set(Some(format!("Connection test failed: {e}")));
                        return;
                    }
                }
            }

            let result = if is_edit {
                update_server(id, name_val, slug_val, url_val, auth_val)
                    .await
                    .map(|_| ())
            } else {
                create_server(name_val, slug_val, url_val, auth_val)
                    .await
                    .map(|_| ())
            };

            set_submitting.set(false);
            match result {
                Ok(()) => on_saved.run(()),
                Err(e) => set_error.set(Some(e.to_string())),
            }
        });
    };

    // Manual test connection button handler
    let on_test_connection = move |_: leptos::ev::MouseEvent| {
        set_error.set(None);
        set_success_msg.set(None);
        set_testing.set(true);

        let url_val = url.get_untracked();
        let auth_val = auth_header.get_untracked();

        leptos::task::spawn_local(async move {
            match test_mcp_connection(url_val, auth_val).await {
                Ok(msg) => set_success_msg.set(Some(msg)),
                Err(e) => set_error.set(Some(format!("Connection test failed: {e}"))),
            }
            set_testing.set(false);
        });
    };

    view! {
        <div class="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4">
            <div class="bg-gray-900 border border-gray-700 rounded-2xl w-full max-w-lg shadow-2xl">
                <div class="px-6 py-4 border-b border-gray-800">
                    <h2 class="text-lg font-semibold text-white">
                        {if is_edit { "Edit Server" } else { "Add Server" }}
                    </h2>
                </div>

                <form on:submit=on_submit class="px-6 py-5 space-y-4">
                    {move || error.get().map(|e| view! {
                        <div class="bg-red-900/30 border border-red-800 text-red-300 text-sm rounded-lg px-4 py-2">
                            {e}
                        </div>
                    })}

                    {move || success_msg.get().map(|msg| view! {
                        <div class="bg-emerald-900/30 border border-emerald-800 text-emerald-300 text-sm rounded-lg px-4 py-2">
                            {msg}
                        </div>
                    })}

                    <div>
                        <label class="block text-sm font-medium text-gray-300 mb-1">"Name"</label>
                        <input
                            type="text"
                            required
                            prop:value=name
                            on:input=on_name_input
                            placeholder="e.g. GitHub MCP"
                            class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                        />
                    </div>

                    <div>
                        <label class="block text-sm font-medium text-gray-300 mb-1">"Slug"</label>
                        <input
                            type="text"
                            required
                            prop:value=slug
                            on:input=on_slug_input
                            placeholder="e.g. github"
                            class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white font-mono text-sm placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                        />
                        <p class="text-xs text-gray-500 mt-1">"Used as the namespace prefix for tools (e.g. github__create_issue)"</p>
                    </div>

                    <div>
                        <label class="block text-sm font-medium text-gray-300 mb-1">"URL"</label>
                        <input
                            type="url"
                            prop:value=url
                            on:input=move |ev| set_url.set(event_target_value(&ev))
                            placeholder="https://mcp.example.com/sse"
                            class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                        />
                    </div>

                    <div>
                        <label class="block text-sm font-medium text-gray-300 mb-1">"Authorization Header"</label>
                        <input
                            type="password"
                            prop:value=auth_header
                            on:input=move |ev| set_auth_header.set(event_target_value(&ev))
                            placeholder="Bearer sk-..."
                            class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                        />
                        <p class="text-xs text-gray-500 mt-1">"Optional. Sent as the Authorization header to the upstream server."</p>
                    </div>

                    <div class="flex justify-end gap-3 pt-3">
                        <button
                            type="button"
                            on:click=move |_| on_cancel.run(())
                            class="px-4 py-2 text-sm text-gray-300 hover:text-white transition-colors"
                        >
                            "Cancel"
                        </button>
                        <button
                            type="button"
                            on:click=on_test_connection
                            disabled=testing
                            class="px-4 py-2 text-sm border border-gray-600 hover:border-gray-500 disabled:opacity-50 text-gray-300 rounded-lg font-medium transition-colors"
                        >
                            {move || if testing.get() { "Testing..." } else { "Test Connection" }}
                        </button>
                        <button
                            type="submit"
                            disabled=submitting
                            class="px-4 py-2 text-sm bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 text-white rounded-lg font-medium transition-colors"
                        >
                            {move || if submitting.get() { "Saving..." } else if is_edit { "Save Changes" } else { "Add Server" }}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    }
}
