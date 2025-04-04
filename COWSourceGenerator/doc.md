﻿# COW entity classes

## Overview

The COW (copy-on-write) principle is applied to DB entities, on the program
level, to enforce mutability invariants, which are normally not enforced by
databases or ORMS.

## General

A COW class has a private Id property, a private default constructor, depending
on type, a private settable TenantId, and a public constructor which takes a
ClientAccessToken or, even better, a reference to an associated (parent) entity
which provides the TenantId. To insert such a type the public constructor must
be used to create an object with zero Id, but a checkable and immutable
TenantId. Otherwise the object can only be instantiated by the DB framework,
which uses the private constructor and generally doesn't care about property
access scoping.

In order to get anything done we'll also need to update entities and their
associated database records. For COW classes we should never update Id and
TenantId properties, but there might also be more properties that should never
change for an entity, and so, should never be updated.

An easy way could be to simply differentiate between mutable and immutable
properties with public/private setters. In the code we simple set the public
properties of the entity and at some point call db.Update, which enumerates all
properties with a public setter an updates those fields in the db. The problem
is that it isn't safe.

As soon as we mutate the entity in the application, we create a scenario where
the application and the database might be in disagreement over what state the
entity is in. This would happen if an exception was thrown during the database
update. In that case the application should rollback it's changes to the entity
in order to retain consistency, something that might not be so easy.

One approach to deal with these kinds over situations is to use
[COW](https://en.wikipedia.org/wiki/Copy-on-write) principle for changes. C#
does it for strings, file systems does it, and we can too.

To enforce true COW we'll need to make the 'mutable' properties of our entity
classes non-public, and, provide a reasonably simple alternative for registering
the desired changes.

We can't simply make the properties _private_, since that indicates total
immutability. _protected_ doesn't really work either in this case. The only
remaining alternative is _internal_, which also fits somewhat ok with the
semantics we're trying to convey.

_protected_ is used to indicate an xyzId property for an associated entity.
Making the id property _protected_, means that the association must be set using
a valid entity, rather than just its Id. Setter methods for these protected
properties are autogenerated if there is a matching id/virtual prop pair, for
instance: public Id<SomePoco> SomeId { get; protected set; } public virtual
SomePoco Some { get; internal set; }

_private protected_ can be used to indicate that the property can only be
modified by code in the entity class itself, or the Ext class. This is useful
for automatic handling of properties like LastModifiedTime. Using _private
protected_ also means that the property is not expected when deserializing.

_protected internal_ is used to indicated that the property can be changed by
code, but shouldn't be set from a deserialized entity object. LastModifiedTime
is again a good example, we want to allow code to explicitly set it (unlike the
automatic handling with private protected), but it should not be copied from
deserialized entities.
