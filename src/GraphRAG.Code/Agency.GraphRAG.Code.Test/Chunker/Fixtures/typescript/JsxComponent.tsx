import React from "react";

export const Component = ({ name }: { name: string }) => <div>{name}</div>;

export class ClassComponent extends React.Component<{ name: string }> {
  render(): JSX.Element {
    return <section>{this.props.name}</section>;
  }
}
