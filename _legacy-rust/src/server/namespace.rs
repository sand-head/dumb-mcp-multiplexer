/// The delimiter between server slug and original name.
pub const SEPARATOR: &str = "__";

/// Prefix a name with a server slug: `github` + `create_issue` → `github__create_issue`
pub fn prefix(slug: &str, name: &str) -> String {
    format!("{slug}{SEPARATOR}{name}")
}

/// Split a prefixed name into (slug, original_name).
/// Returns None if the name doesn't contain the separator.
pub fn split(prefixed_name: &str) -> Option<(&str, &str)> {
    prefixed_name.split_once(SEPARATOR)
}

/// Prefix a URI with the server slug for resource namespacing.
/// Uses a custom scheme: `proxy://{slug}/{original_uri}`
pub fn prefix_uri(slug: &str, uri: &str) -> String {
    format!("proxy://{slug}/{uri}")
}

/// Split a prefixed URI back into (slug, original_uri).
/// Expects format: `proxy://{slug}/{rest}`
pub fn split_uri(prefixed_uri: &str) -> Option<(&str, &str)> {
    let rest = prefixed_uri.strip_prefix("proxy://")?;
    let (slug, uri) = rest.split_once('/')?;
    Some((slug, uri))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_prefix_and_split() {
        assert_eq!(prefix("github", "create_issue"), "github__create_issue");
        assert_eq!(
            split("github__create_issue"),
            Some(("github", "create_issue"))
        );
        assert_eq!(split("no_separator"), None);
    }

    #[test]
    fn test_prefix_uri_and_split() {
        let uri = "file:///config.json";
        let prefixed = prefix_uri("myserver", uri);
        assert_eq!(prefixed, "proxy://myserver/file:///config.json");
        assert_eq!(
            split_uri(&prefixed),
            Some(("myserver", "file:///config.json"))
        );
    }
}
